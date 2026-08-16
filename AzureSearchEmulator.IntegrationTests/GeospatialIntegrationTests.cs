using Azure.Core.GeoJson;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for Edm.GeographyPoint support (issue #5), run against a containerized
/// emulator through the Azure Search SDK.
/// </summary>
/// <remarks>
/// These use real city coordinates so the expected results correspond to real distances.
/// Measured from Seattle, the reference set is: Bellevue ~9.8km, Tacoma ~40.2km,
/// Vancouver BC ~195.3km, Portland ~234.0km, San Francisco ~1093.2km, New York ~3865.5km,
/// Tokyo ~7696.3km, London ~7699.6km, and Sydney ~12470.3km.
///
/// Going through the SDK rather than raw JSON matters here: the SDK serializes
/// <see cref="GeoPoint"/> into the GeoJSON shape Azure Search expects and parses it back
/// out again, so a longitude/latitude transposition anywhere in the round trip would surface
/// as a wildly wrong distance rather than passing unnoticed.
/// </remarks>
public class GeospatialIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>Seattle, the origin used for the distance assertions.</summary>
    private const string SeattlePoint = "geography'POINT(-122.3321 47.6062)'";

    [Fact]
    public async Task GeoDistance_WithinTenKilometers_ReturnsOnlyNearbyCities()
    {
        const string indexName = "test-geo-distance-near";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // Bellevue is ~9.8km from Seattle; Tacoma, the next nearest, is ~40.2km.
        var options = new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) le 10",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["bellevue", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoDistance_WithinTwoHundredFiftyKilometers_ReturnsPacificNorthwestCities()
    {
        const string indexName = "test-geo-distance-region";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // 250km reaches Portland (~234km) and Vancouver BC (~195km) but not San Francisco.
        var options = new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) le 250",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["bellevue", "portland", "seattle", "tacoma", "vancouver"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoDistance_GreaterThan_ReturnsOnlyDistantCities()
    {
        const string indexName = "test-geo-distance-far";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // Beyond 5000km leaves only the intercontinental cities. Atlantis has no location,
        // and a null point matches neither direction of the comparison.
        var options = new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) gt 5000",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["london", "sydney", "tokyo"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoDistance_CombinedWithScalarFilter_AppliesBoth()
    {
        const string indexName = "test-geo-distance-combined";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // Within 250km of Seattle and larger than half a million people: Portland and
        // Vancouver BC qualify, Seattle itself qualifies, but Bellevue and Tacoma are
        // too small.
        var options = new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) le 250 and Population gt 500000",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["portland", "seattle", "vancouver"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoDistance_NullLocation_MatchesNeitherDirection()
    {
        const string indexName = "test-geo-distance-null";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // Azure Search evaluates geo.distance over a null field as null, which fails every
        // comparison, so a radius covering the whole planet still excludes Atlantis.
        var within = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) le 30000",
            Size = 50
        });

        var beyond = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = $"geo.distance(Location, {SeattlePoint}) gt 30000",
            Size = 50
        });

        Assert.DoesNotContain("atlantis", within);
        Assert.DoesNotContain("atlantis", beyond);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoIntersects_PugetSoundPolygon_ReturnsCitiesInside()
    {
        const string indexName = "test-geo-intersects";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // A counterclockwise box over the Puget Sound area: it covers Seattle, Bellevue and
        // Tacoma, but stops short of Vancouver BC to the north and Portland to the south.
        var options = new SearchOptions
        {
            Filter = "geo.intersects(Location, geography'POLYGON((-122.6 47.1, -122.0 47.1, -122.0 47.8, -122.6 47.8, -122.6 47.1))')",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["bellevue", "seattle", "tacoma"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeoIntersects_PolygonAcrossTheAntimeridian_ReturnsCitiesOnBothSides()
    {
        const string indexName = "test-geo-intersects-dateline";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);

        // Two real places either side of the dateline in Fiji, plus one far away.
        var documents = new List<City>
        {
            new() { Id = "taveuni", Name = "Taveuni", Country = "FJ", Location = new GeoPoint(179.9700, -16.8500), Population = 9000 },
            new() { Id = "vatoa", Name = "Vatoa", Country = "FJ", Location = new GeoPoint(-178.8200, -19.8300), Population = 300 },
            new() { Id = "suva", Name = "Suva", Country = "FJ", Location = new GeoPoint(178.4419, -18.1416), Population = 93970 },
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, documents.Count);

        // The dateline-spanning rectangle form Azure documents as supported. Read naively
        // its longitudes run -178.8 to 179.97 and would select Suva instead of the islands
        // either side of the meridian.
        var options = new SearchOptions
        {
            Filter = "geo.intersects(Location, geography'POLYGON((179 -21, 179 -15, -178 -15, -178 -21, 179 -21))')",
            Size = 50
        };

        var ids = await SearchIdsAsync(searchClient, options);

        Assert.Equal(["taveuni", "vatoa"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderByGeoDistance_SortsByRealDistanceFromSeattle()
    {
        const string indexName = "test-geo-orderby";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        var options = new SearchOptions
        {
            OrderBy = { $"geo.distance(Location, {SeattlePoint}) asc" },
            Size = 50
        };

        var results = await searchClient.SearchAsync<City>("*", options, TestContext.Current.CancellationToken);
        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .ToList();

        // Nearest to farthest by real great-circle distance. Tokyo edges out London by
        // about 3km, which a correct haversine gets right and a rough approximation may not.
        // Atlantis sorts last because a null point counts as the maximum distance.
        Assert.Equal(
            ["seattle", "bellevue", "tacoma", "vancouver", "portland", "sanfrancisco", "newyork", "tokyo", "london", "sydney", "atlantis"],
            ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderByGeoDistance_Descending_PutsNullLocationFirst()
    {
        const string indexName = "test-geo-orderby-desc";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        var options = new SearchOptions
        {
            OrderBy = { $"geo.distance(Location, {SeattlePoint}) desc" },
            Size = 3
        };

        var results = await searchClient.SearchAsync<City>("*", options, TestContext.Current.CancellationToken);
        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .ToList();

        // A null location sorts as the maximum distance, so it leads under desc, ahead of
        // Sydney and London. Size is deliberately smaller than the result set so the sort
        // has to evict correctly rather than just ordering everything it collected.
        Assert.Equal(["atlantis", "sydney", "london"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderByGeoDistance_AfterAnotherField_AppliesBothClauses()
    {
        const string indexName = "test-geo-orderby-multi";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        // The comma inside geo.distance's argument list must not be mistaken for a clause
        // separator. Country groups the results; distance orders within each group.
        var options = new SearchOptions
        {
            Filter = "Country eq 'US'",
            OrderBy = { "Country asc", $"geo.distance(Location, {SeattlePoint}) asc" },
            Size = 50
        };

        var results = await searchClient.SearchAsync<City>("*", options, TestContext.Current.CancellationToken);
        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .ToList();

        Assert.Equal(["seattle", "bellevue", "tacoma", "portland", "sanfrancisco", "newyork"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeographyPoint_RoundTripsThroughTheSdkWithCoordinatesIntact()
    {
        const string indexName = "test-geo-roundtrip";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<City>(
            "sydney",
            cancellationToken: TestContext.Current.CancellationToken);

        // Sydney is in the southern hemisphere at a positive longitude, so a transposed or
        // sign-flipped coordinate could not survive this assertion unnoticed.
        Assert.NotNull(document.Value.Location);
        Assert.Equal(151.2093, document.Value.Location!.Coordinates.Longitude, 4);
        Assert.Equal(-33.8688, document.Value.Location.Coordinates.Latitude, 4);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GeographyPoint_NullLocation_RoundTripsAsNull()
    {
        const string indexName = "test-geo-roundtrip-null";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateCityIndexAsync(indexClient, indexName);
        await UploadCitiesAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<City>(
            "atlantis",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(document.Value.Location);
        Assert.Equal("Atlantis", document.Value.Name);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs a search and returns the matching ids, sorted so assertions do not depend on
    /// the order results happen to come back in.
    /// </summary>
    private static async Task<List<string>> SearchIdsAsync(SearchClient searchClient, SearchOptions options)
    {
        var results = await searchClient.SearchAsync<City>("*", options, TestContext.Current.CancellationToken);

        return (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<SearchIndex> CreateCityIndexAsync(SearchIndexClient indexClient, string indexName)
    {
        try
        {
            await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // expected
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SearchField(nameof(City.Id), SearchFieldDataType.String) { IsKey = true, IsStored = true, IsFilterable = true },
                new SearchField(nameof(City.Name), SearchFieldDataType.String) { IsSearchable = true, IsStored = true },
                new SearchField(nameof(City.Country), SearchFieldDataType.String) { IsFilterable = true, IsSortable = true, IsStored = true },
                new SearchField(nameof(City.Location), SearchFieldDataType.GeographyPoint) { IsFilterable = true, IsSortable = true, IsStored = true },
                new SearchField(nameof(City.Population), SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true, IsStored = true },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        return index;
    }

    private static async Task UploadCitiesAsync(SearchClient searchClient)
    {
        // Real coordinates and populations, so the filters below correspond to real
        // distances rather than to arbitrary test values. GeoPoint takes longitude first.
        var documents = new List<City>
        {
            new() { Id = "seattle", Name = "Seattle", Country = "US", Location = new GeoPoint(-122.3321, 47.6062), Population = 737015 },
            new() { Id = "bellevue", Name = "Bellevue", Country = "US", Location = new GeoPoint(-122.2015, 47.6101), Population = 148164 },
            new() { Id = "tacoma", Name = "Tacoma", Country = "US", Location = new GeoPoint(-122.4443, 47.2529), Population = 219346 },
            new() { Id = "portland", Name = "Portland", Country = "US", Location = new GeoPoint(-122.6784, 45.5152), Population = 652503 },
            new() { Id = "vancouver", Name = "Vancouver", Country = "CA", Location = new GeoPoint(-123.1207, 49.2827), Population = 675218 },
            new() { Id = "sanfrancisco", Name = "San Francisco", Country = "US", Location = new GeoPoint(-122.4194, 37.7749), Population = 873965 },
            new() { Id = "newyork", Name = "New York", Country = "US", Location = new GeoPoint(-74.0060, 40.7128), Population = 8336817 },
            new() { Id = "london", Name = "London", Country = "GB", Location = new GeoPoint(-0.1276, 51.5074), Population = 8982000 },
            new() { Id = "tokyo", Name = "Tokyo", Country = "JP", Location = new GeoPoint(139.6917, 35.6895), Population = 13960000 },
            new() { Id = "sydney", Name = "Sydney", Country = "AU", Location = new GeoPoint(151.2093, -33.8688), Population = 5312000 },
            // A city with no location at all, covering the null-handling rules.
            new() { Id = "atlantis", Name = "Atlantis", Country = "XX", Location = null, Population = 0 },
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, documents.Count);
    }

    /// <summary>
    /// Waits for indexed documents to become visible to searches.
    /// </summary>
    /// <remarks>
    /// Indexing commits before returning, but the searcher's reader is refreshed
    /// independently, so a search issued immediately afterwards can still observe the
    /// previous state.
    /// </remarks>
    private static async Task WaitForCountAsync(SearchClient searchClient, int expected)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var count = await searchClient.GetDocumentCountAsync(TestContext.Current.CancellationToken);

            if (count.Value >= expected)
            {
                return;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Only {expected} documents were expected to become searchable, but the count never reached it.");
    }
}
