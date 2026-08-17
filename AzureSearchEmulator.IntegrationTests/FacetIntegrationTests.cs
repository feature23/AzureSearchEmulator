using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for faceted search (issue #43), run against a containerized emulator
/// through the Azure Search SDK.
/// </summary>
/// <remarks>
/// The index and the facet expressions here follow Microsoft's <c>hotels-sample</c> examples —
/// <c>Category</c>, <c>Tags,count:5</c>, <c>Rating,values:1|2|3|4|5</c>,
/// <c>Address/City,count:5</c>, <c>Rooms/BaseRate,values:80|150|220</c> — from
/// https://learn.microsoft.com/en-us/azure/search/search-faceted-navigation-examples.
///
/// Going through the SDK rather than raw HTTP is the point of these: the SDK deserializes
/// <c>@search.facets</c> into <see cref="FacetResult"/>, and it only does that correctly if the
/// emulator emits the documented wire shape — buckets keyed <c>value</c>/<c>count</c>, and
/// range buckets that <em>omit</em> <c>from</c> on the first and <c>to</c> on the last rather
/// than sending nulls. A response that were merely plausible would still deserialize into
/// empty or wrongly-typed facets here.
/// </remarks>
public class FacetIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task Facet_OnCategory_ReturnsBucketPerValue()
    {
        const string indexName = "test-facet-category";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var facets = await FacetAsync(searchClient, "*", null, ["Category"]);

        var categories = facets["Category"];

        // Every hotel falls in exactly one category, so the buckets partition the corpus.
        Assert.Equal(6, categories.Sum(f => f.Count));

        Assert.Equal(
            [("Boutique", 2L), ("Budget", 2L), ("Luxury", 1L), ("Suite", 1L)],
            categories
                .Select(f => ((string)f.Value, f.Count!.Value))
                .OrderBy(p => p.Item1, StringComparer.Ordinal)
                .ToArray());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_OnCollection_CountsEachTag()
    {
        const string indexName = "test-facet-collection";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var facets = await FacetAsync(searchClient, "*", null, ["Tags"]);

        var tags = facets["Tags"].ToDictionary(f => (string)f.Value, f => f.Count!.Value);

        // A document appears in every tag bucket it carries, so these overlap.
        Assert.Equal(4, tags["pool"]);
        Assert.Equal(2, tags["view"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_WithCountOption_LimitsBuckets()
    {
        const string indexName = "test-facet-count";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' "Tags,count:5" example.
        var facets = await FacetAsync(searchClient, "*", null, ["Tags,count:5"]);

        Assert.Equal(5, facets["Tags"].Count);

        // Descending by count is the default, so the largest bucket survives the cut.
        Assert.Equal(4, facets["Tags"][0].Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_WithSortOption_OrdersBuckets()
    {
        const string indexName = "test-facet-sort";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var facets = await FacetAsync(searchClient, "*", null, ["Category,sort:value"]);

        var values = facets["Category"].Select(f => (string)f.Value).ToArray();

        Assert.Equal(values.OrderBy(v => v, StringComparer.Ordinal).ToArray(), values);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_RangeWithValues_ProducesOpenEndedOuterBuckets()
    {
        const string indexName = "test-facet-values";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' "Rating,values:1|2|3|4|5" example: five bounds, six buckets.
        var facets = await FacetAsync(searchClient, "*", null, ["Rating,values:1|2|3|4|5"]);

        var buckets = facets["Rating"];

        Assert.Equal(6, buckets.Count);

        // The SDK exposes the documented shape as From/To, and only an omitted bound comes
        // back null — which is how the outermost buckets are recognized as open-ended.
        Assert.Null(buckets[0].From);
        Assert.Equal(1.0, Convert.ToDouble(buckets[0].To));

        Assert.Equal(1.0, Convert.ToDouble(buckets[1].From));
        Assert.Equal(2.0, Convert.ToDouble(buckets[1].To));

        Assert.Equal(5.0, Convert.ToDouble(buckets[^1].From));
        Assert.Null(buckets[^1].To);

        // Ratings run 2.5 to 4.9 here, so nothing lands in the outermost buckets, but they
        // are still reported: a range facet describes a fixed scale.
        Assert.Equal(0, buckets[0].Count);
        Assert.Equal(0, buckets[^1].Count);
        Assert.Equal(6, buckets.Sum(f => f.Count));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_RangeOnComplexCollectionSubField_CountsHotelsNotRooms()
    {
        const string indexName = "test-facet-rooms-baserate";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' "Rooms/BaseRate,values:80|150|220" example. The Seattle hotel has two
        // rooms under 150 (96 and 120): different values in one bucket, so it must still
        // count once there — facets count the parent document, not the sub-documents.
        var facets = await FacetAsync(searchClient, "*", null, ["Rooms/BaseRate,values:80|150|220"]);

        var buckets = facets["Rooms/BaseRate"];

        Assert.Equal(4, buckets.Count);
        Assert.Null(buckets[0].From);
        Assert.Null(buckets[^1].To);

        // Only the budget hotel has a room under 80.
        Assert.Equal(1, buckets[0].Count);

        // Seattle contributes exactly one to the 80–150 bucket despite its two rooms there.
        Assert.Equal(3, buckets[1].Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_OnComplexSubField_CountsByPath()
    {
        const string indexName = "test-facet-city";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' "Address/City,count:5" example.
        var facets = await FacetAsync(searchClient, "*", null, ["Address/City,count:5"]);

        var cities = facets["Address/City"].ToDictionary(f => (string)f.Value, f => f.Count!.Value);

        Assert.Equal(2, cities["Seattle"]);
        Assert.Equal(1, cities["Portland"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_IsNarrowedByFilter()
    {
        const string indexName = "test-facet-filter";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Facets are computed from the current results, so a filter narrows them too.
        var facets = await FacetAsync(searchClient, "*", "Category eq 'Budget'", ["Address/City"]);

        Assert.Equal(2, facets["Address/City"].Sum(f => f.Count));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_CountsWholeMatchSet_NotJustThePage()
    {
        const string indexName = "test-facet-paging";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var options = new SearchOptions { Size = 1, Facets = { "Category" } };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var docs = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        // One document on the page, but the facets still describe all six.
        Assert.Single(docs);
        Assert.Equal(6, response.Value.Facets["Category"].Sum(f => f.Count));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_WithSizeZero_ReturnsFacetsWithoutDocuments()
    {
        const string indexName = "test-facet-size-zero";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' distinct-values pattern: top of zero to get just the facet structure.
        var options = new SearchOptions
        {
            Size = 0,
            IncludeTotalCount = true,
            Facets = { "Category" },
        };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var docs = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(docs);
        Assert.Equal(6, response.Value.TotalCount);
        Assert.Equal(6, response.Value.Facets["Category"].Sum(f => f.Count));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_MultipleFacetsInOneRequest()
    {
        const string indexName = "test-facet-multiple";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The docs' three-facet example, mixing a plain facet, a count override, and a range.
        var facets = await FacetAsync(
            searchClient, "*", null, ["Category", "Tags,count:5", "Rating,values:1|2|3|4|5"]);

        Assert.Equal(["Category", "Rating", "Tags"], facets.Keys.Order(StringComparer.Ordinal));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Search_WithoutFacets_OmitsFacetsFromResponse()
    {
        const string indexName = "test-facet-absent";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", new SearchOptions { Size = 50 }, TestContext.Current.CancellationToken);

        // No facets asked for, so no facet structure comes back at all.
        Assert.Null(response.Value.Facets);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Facet_OnNonFacetableField_ReturnsError()
    {
        const string indexName = "test-facet-not-facetable";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // HotelName is not facetable, which Azure Search rejects rather than ignoring.
        var options = new SearchOptions { Size = 50, Facets = { "HotelName" } };

        await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => searchClient.SearchAsync<SearchDocument>(
                "*", options, TestContext.Current.CancellationToken));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs a faceted search and returns just the facet structure the server sent.
    /// </summary>
    private static async Task<IDictionary<string, IList<FacetResult>>> FacetAsync(
        SearchClient searchClient,
        string search,
        string? filter,
        IEnumerable<string> facets)
    {
        var options = new SearchOptions { Size = 50, Filter = filter };

        foreach (var facet in facets)
        {
            options.Facets.Add(facet);
        }

        var response = await searchClient.SearchAsync<SearchDocument>(
            search, options, TestContext.Current.CancellationToken);

        Assert.NotNull(response.Value.Facets);

        return response.Value.Facets;
    }

    /// <summary>
    /// The <c>hotels-sample</c> schema, with <c>facetable</c> set on the low-cardinality
    /// fields Microsoft's article recommends it for.
    /// </summary>
    private static async Task CreateHotelIndexAsync(SearchIndexClient indexClient, string indexName)
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
                new SimpleField("HotelId", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                // Deliberately not facetable: a unique-per-document string is exactly what the
                // docs warn against faceting on.
                new SearchableField("HotelName") { IsFilterable = true },
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SearchableField("Tags", collection: true) { IsFilterable = true, IsFacetable = true },
                new SimpleField("Rating", SearchFieldDataType.Double) { IsFilterable = true, IsFacetable = true },
                new ComplexField("Address")
                {
                    Fields =
                    {
                        new SimpleField("City", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                        new SimpleField("StateProvince", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                    }
                },
                new ComplexField("Rooms", collection: true)
                {
                    Fields =
                    {
                        new SearchableField("Type") { IsFacetable = true },
                        new SimpleField("BaseRate", SearchFieldDataType.Double) { IsFilterable = true, IsFacetable = true },
                    }
                },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    private static async Task UploadHotelsAsync(SearchClient searchClient)
    {
        var documents = new List<object>
        {
            new
            {
                HotelId = "1",
                HotelName = "Stay-Kay City Hotel",
                Category = "Boutique",
                Tags = new[] { "pool", "air conditioning", "concierge" },
                Rating = 3.6,
                Address = new { City = "Seattle", StateProvince = "WA" },
                // Two rooms in the 80–150 band, to pin the parent-document counting rule.
                Rooms = new[]
                {
                    new { Type = "Budget Room", BaseRate = 96.99 },
                    new { Type = "Deluxe Room", BaseRate = 120.99 },
                },
            },
            new
            {
                HotelId = "2",
                HotelName = "Old Century Hotel",
                Category = "Boutique",
                Tags = new[] { "pool", "free wifi", "concierge" },
                Rating = 3.6,
                Address = new { City = "Sarasota", StateProvince = "FL" },
                Rooms = new[]
                {
                    new { Type = "Budget Room", BaseRate = 80.99 },
                    new { Type = "Standard Room", BaseRate = 180.99 },
                },
            },
            new
            {
                HotelId = "3",
                HotelName = "Gastronomic Landscape Hotel",
                Category = "Suite",
                Tags = new[] { "restaurant", "bar", "continental breakfast" },
                Rating = 4.8,
                Address = new { City = "Seattle", StateProvince = "WA" },
                Rooms = new[]
                {
                    new { Type = "Suite", BaseRate = 250.99 },
                },
            },
            new
            {
                HotelId = "4",
                HotelName = "Red Tide Hotel",
                Category = "Budget",
                Tags = new[] { "pool", "free wifi" },
                Rating = 2.5,
                Address = new { City = "Tampa", StateProvince = "FL" },
                Rooms = new[]
                {
                    new { Type = "Budget Room", BaseRate = 60.99 },
                },
            },
            new
            {
                HotelId = "5",
                HotelName = "Windy Ocean Motel",
                Category = "Budget",
                Tags = new[] { "pool", "air conditioning", "view" },
                Rating = 3.2,
                Address = new { City = "Portland", StateProvince = "OR" },
                Rooms = new[]
                {
                    new { Type = "Suite", BaseRate = 175.99 },
                    new { Type = "Standard Room", BaseRate = 110.99 },
                },
            },
            new
            {
                HotelId = "6",
                HotelName = "Triple Landscape Hotel",
                Category = "Luxury",
                Tags = new[] { "bar", "view", "concierge" },
                Rating = 4.8,
                Address = new { City = "San Antonio", StateProvince = "TX" },
                Rooms = new[]
                {
                    new { Type = "Deluxe Room", BaseRate = 230.99 },
                },
            },
        };

        var batch = IndexDocumentsBatch.Upload(documents);
        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
    }
}
