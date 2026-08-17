using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for Edm.ComplexType and Collection(Edm.ComplexType) support (issue #7),
/// run against a containerized emulator through the Azure Search SDK.
/// </summary>
/// <remarks>
/// These go through the real SDK rather than raw JSON so that the whole path is covered: the
/// SDK builds the <see cref="ComplexField"/> schema on index creation, serializes nested
/// objects into the wire format Azure Search expects, and deserializes search results back
/// into the model. A sub-field indexed under the wrong name, or a complex value that could
/// not be reassembled from its flattened parts, surfaces here as a failed filter or a lost
/// document rather than passing silently.
/// </remarks>
public class ComplexTypeIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task Filter_OnComplexSubFieldPath_ReturnsMatchingHotels()
    {
        const string indexName = "test-complex-subfield-filter";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Address/City eq 'Seattle'",
            Size = 50
        });

        Assert.Equal(["empty", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_OnNestedComplexSubFieldPath_ReturnsMatchingHotels()
    {
        const string indexName = "test-complex-nested-filter";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Address/Geo/Lat is two levels deep, so this fails unless multi-level paths resolve.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Address/Geo/Lat gt 47.5",
            Size = 50
        });

        Assert.Equal(["bellevue", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_CombiningComplexSubFieldWithTopLevelField_AppliesBoth()
    {
        const string indexName = "test-complex-combined-filter";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Address/City eq 'Seattle' and Address/PostalCode eq '98101'",
            Size = 50
        });

        Assert.Equal(["seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_OnComplexCollection_MatchesWhenAnyElementQualifies()
    {
        const string indexName = "test-complex-any";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/Type eq 'Deluxe')",
            Size = 50
        });

        Assert.Equal(["seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_OnComplexCollection_MatchesViaNonFirstElement()
    {
        const string indexName = "test-complex-any-second";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // "Standard" is the Seattle hotel's second room, which catches an implementation
        // that only ever considers a collection's first element.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/Type eq 'Standard')",
            Size = 50
        });

        Assert.Equal(["portland", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_OnComplexCollectionNumericSubField_MatchesByRange()
    {
        const string indexName = "test-complex-any-range";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The literal 130 is written as an integer but BaseRate is Edm.Double, so this also
        // covers the numeric widening a range query needs to match doubles at all.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/BaseRate lt 130)",
            Size = 50
        });

        Assert.Equal(["portland", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task All_OnComplexCollection_RequiresEveryElementToQualify()
    {
        const string indexName = "test-complex-all";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Bellevue's only room is a Suite. Both room-less hotels match vacuously, the way
        // "all" over an empty collection does in OData.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/all(r: r/Type ne 'Standard')",
            Size = 50
        });

        Assert.Equal(["bellevue", "empty", "noaddress"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_WithTwoCriteria_CorrelatesThemToTheSameRoom()
    {
        const string indexName = "test-complex-correlation";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The documented example. Criteria inside one lambda apply to the same element, so
        // this returns hotels with at least one deluxe room that is itself under 130.
        // Seattle's Deluxe room costs 250 and its cheap room is a Standard, so it must not
        // match — that combination is precisely what uncorrelated evaluation gets wrong.
        // https://learn.microsoft.com/en-us/azure/search/search-query-understand-collection-filters#correlated-versus-uncorrelated-search
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/Type eq 'Deluxe' and r/BaseRate lt 130)",
            Size = 50
        });

        Assert.Empty(ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_WithTwoCriteria_MatchesWhenOneRoomSatisfiesBoth()
    {
        const string indexName = "test-complex-correlation-match";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Portland's two Standard rooms cost 90 and 95, so a single room satisfies both
        // criteria. The counterpart to the test above: correlation must not reject genuine
        // matches either.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/Type eq 'Standard' and r/BaseRate lt 100)",
            Size = 50
        });

        Assert.Equal(["portland"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task All_WithEquality_IsSupportedOverAComplexCollection()
    {
        const string indexName = "test-complex-all-equality";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Documented as valid in the $filter reference:
        // "$filter=ParkingIncluded eq true and Rooms/all(room: room/SmokingAllowed eq false)"
        // Seattle's rooms are both non-smoking; Bellevue and Portland each have a smoking
        // room. The two room-less hotels match vacuously.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/all(r: r/SmokingAllowed eq false)",
            Size = 50
        });

        Assert.Equal(["empty", "noaddress", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnyWithNoLambda_DistinguishesEmptyFromNonEmptyCollections()
    {
        const string indexName = "test-complex-any-empty";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // "Rooms/any()" and "not Rooms/any()" are the documented way to test a complex
        // collection for elements.
        var nonEmpty = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any()",
            Size = 50
        });

        var empty = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "not Rooms/any()",
            Size = 50
        });

        Assert.Equal(["bellevue", "portland", "seattle"], nonEmpty);
        Assert.Equal(["empty", "noaddress"], empty);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Any_OnPrimitiveCollectionNestedInComplexCollection_Matches()
    {
        const string indexName = "test-complex-nested-collection";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Rooms/Tags is a Collection(Edm.String) inside a Collection(Edm.ComplexType), so
        // the inner lambda variable has to resolve through the outer one.
        var ids = await SearchIdsAsync(searchClient, new SearchOptions
        {
            Filter = "Rooms/any(r: r/Tags/any(t: t eq 'kitchen'))",
            Size = 50
        });

        Assert.Equal(["bellevue"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ComplexType_RoundTripsThroughTheSdkWithShapeIntact()
    {
        const string indexName = "test-complex-roundtrip";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<Hotel>(
            "seattle",
            cancellationToken: TestContext.Current.CancellationToken);

        var hotel = document.Value;

        Assert.NotNull(hotel.Address);
        Assert.Equal("1 Pike Place", hotel.Address!.Street);
        Assert.Equal("Seattle", hotel.Address.City);
        Assert.Equal("98101", hotel.Address.PostalCode);

        Assert.NotNull(hotel.Address.Geo);
        Assert.Equal(47.6062, hotel.Address.Geo!.Lat, 4);
        Assert.Equal(-122.3321, hotel.Address.Geo.Lon, 4);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ComplexCollection_RoundTripsPreservingPerElementGrouping()
    {
        const string indexName = "test-complex-collection-roundtrip";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<Hotel>(
            "seattle",
            cancellationToken: TestContext.Current.CancellationToken);

        var rooms = document.Value.Rooms;

        Assert.Equal(2, rooms.Length);

        // Each rate has to stay attached to its own room. Flattened leaf values alone could
        // not say whether 250 belonged to the Deluxe room or the Standard one.
        Assert.Equal("Deluxe", rooms[0].Type);
        Assert.Equal(250.0, rooms[0].BaseRate);
        Assert.Equal(["wifi", "view"], rooms[0].Tags);

        Assert.Equal("Standard", rooms[1].Type);
        Assert.Equal(120.0, rooms[1].BaseRate);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ComplexType_NullValue_RoundTripsAsNull()
    {
        const string indexName = "test-complex-null";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<Hotel>(
            "noaddress",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(document.Value.Address);
        Assert.Equal("No Address Inn", document.Value.Name);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EmptyComplexCollection_RoundTripsAsEmpty()
    {
        const string indexName = "test-complex-empty-collection";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var document = await searchClient.GetDocumentAsync<Hotel>(
            "empty",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(document.Value.Rooms);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Search_MatchesTextInSearchableSubField()
    {
        const string indexName = "test-complex-search";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // "Burnside" appears only in the Portland hotel's street, a searchable sub-field.
        var results = await searchClient.SearchAsync<Hotel>(
            "Burnside",
            new SearchOptions { Size = 50 },
            TestContext.Current.CancellationToken);

        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .ToList();

        Assert.Equal(["portland"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Search_RestrictedToSubFieldPath_OnlySearchesThatField()
    {
        const string indexName = "test-complex-searchfields";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // "Seattle" is in one hotel's Name but in two hotels' Address/City, so restricting
        // to the sub-field must widen the match rather than fall back to the name.
        var results = await searchClient.SearchAsync<Hotel>(
            "Seattle",
            new SearchOptions { SearchFields = { "Address/City" }, Size = 50 },
            TestContext.Current.CancellationToken);

        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["empty", "seattle"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OrderBy_NestedComplexSubField_SortsByThatSubField()
    {
        const string indexName = "test-complex-orderby";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var results = await searchClient.SearchAsync<Hotel>(
            "*",
            new SearchOptions
            {
                Filter = "Address/Geo/Lat gt 0",
                OrderBy = { "Address/Geo/Lat asc" },
                Size = 50
            },
            TestContext.Current.CancellationToken);

        var ids = (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .ToList();

        // Portland (45.5152), Empty Inn (47.0), Seattle (47.6062), Bellevue (47.6101).
        Assert.Equal(["portland", "empty", "seattle", "bellevue"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateIndex_RoundTripsTheComplexSchema()
    {
        const string indexName = "test-complex-schema";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateHotelIndexAsync(indexClient, indexName);

        // Reading the schema back proves sub-fields survive persistence, not just the
        // in-memory object that was posted.
        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        var address = index.Value.Fields.Single(f => f.Name == "Address");
        Assert.Equal(SearchFieldDataType.Complex, address.Type);
        Assert.Contains(address.Fields, f => f.Name == "City");

        var geo = address.Fields.Single(f => f.Name == "Geo");
        Assert.Equal(SearchFieldDataType.Complex, geo.Type);
        Assert.Contains(geo.Fields, f => f.Name == "Lat");

        var rooms = index.Value.Fields.Single(f => f.Name == "Rooms");
        Assert.Equal(SearchFieldDataType.Collection(SearchFieldDataType.Complex), rooms.Type);
        Assert.Contains(rooms.Fields, f => f.Name == "BaseRate");

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Runs a search and returns the matching ids, sorted so assertions do not depend on
    /// the order results happen to come back in.
    /// </summary>
    private static async Task<List<string>> SearchIdsAsync(SearchClient searchClient, SearchOptions options)
    {
        var results = await searchClient.SearchAsync<Hotel>("*", options, TestContext.Current.CancellationToken);

        return (await results.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

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
                new SearchField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchField("Name", SearchFieldDataType.String) { IsSearchable = true },
                new ComplexField("Address")
                {
                    Fields =
                    {
                        new SearchableField("Street"),
                        new SearchableField("City") { IsFilterable = true },
                        new SimpleField("PostalCode", SearchFieldDataType.String) { IsFilterable = true },
                        new ComplexField("Geo")
                        {
                            Fields =
                            {
                                new SimpleField("Lat", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
                                new SimpleField("Lon", SearchFieldDataType.Double) { IsFilterable = true },
                            }
                        },
                    }
                },
                new ComplexField("Rooms", collection: true)
                {
                    Fields =
                    {
                        new SearchableField("Type") { IsFilterable = true },
                        new SimpleField("BaseRate", SearchFieldDataType.Double) { IsFilterable = true },
                        new SimpleField("SmokingAllowed", SearchFieldDataType.Boolean) { IsFilterable = true },
                        new SearchableField("Tags", collection: true) { IsFilterable = true },
                    }
                },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    private static async Task UploadHotelsAsync(SearchClient searchClient)
    {
        var documents = new List<Hotel>
        {
            new()
            {
                Id = "seattle",
                Name = "Seattle Downtown",
                Address = new Address
                {
                    Street = "1 Pike Place",
                    City = "Seattle",
                    PostalCode = "98101",
                    Geo = new GeoCoordinates { Lat = 47.6062, Lon = -122.3321 },
                },
                Rooms =
                [
                    new Room { Type = "Deluxe", BaseRate = 250.0, SmokingAllowed = false, Tags = ["wifi", "view"] },
                    new Room { Type = "Standard", BaseRate = 120.0, SmokingAllowed = false, Tags = ["wifi"] },
                ],
            },
            new()
            {
                Id = "bellevue",
                Name = "Bellevue Suites",
                Address = new Address
                {
                    Street = "500 Bellevue Way",
                    City = "Bellevue",
                    PostalCode = "98004",
                    Geo = new GeoCoordinates { Lat = 47.6101, Lon = -122.2015 },
                },
                Rooms =
                [
                    new Room { Type = "Suite", BaseRate = 400.0, SmokingAllowed = true, Tags = ["kitchen", "wifi"] },
                ],
            },
            new()
            {
                Id = "portland",
                Name = "Portland Budget",
                Address = new Address
                {
                    Street = "9 Burnside",
                    City = "Portland",
                    PostalCode = "97209",
                    Geo = new GeoCoordinates { Lat = 45.5152, Lon = -122.6784 },
                },
                Rooms =
                [
                    new Room { Type = "Standard", BaseRate = 90.0, SmokingAllowed = true, Tags = ["parking"] },
                    new Room { Type = "Standard", BaseRate = 95.0, SmokingAllowed = false, Tags = [] },
                ],
            },
            // No rooms at all, so the lambda operators have an empty collection to reckon with.
            new()
            {
                Id = "empty",
                Name = "Empty Inn",
                Address = new Address
                {
                    Street = "0 Nowhere",
                    City = "Seattle",
                    PostalCode = "98999",
                    Geo = new GeoCoordinates { Lat = 47.0, Lon = -122.0 },
                },
                Rooms = [],
            },
            // No address at all, covering a null complex value.
            new()
            {
                Id = "noaddress",
                Name = "No Address Inn",
                Address = null,
                Rooms = [],
            },
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, documents.Count);
    }

    /// <summary>
    /// Waits for indexed documents to become visible to searches, since indexing commits
    /// and the searcher's reader refreshes independently.
    /// </summary>
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
