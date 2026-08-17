using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for <c>$select</c> field projection (issue #42), run against a
/// containerized emulator through the Azure Search SDK.
/// </summary>
/// <remarks>
/// These deliberately deserialize into <see cref="SearchDocument"/> — the SDK's untyped
/// dictionary — rather than into the <see cref="Hotel"/> model. A typed model cannot show
/// that projection happened: a field the server never sent and a field that came back null
/// both land as null on the model, so an emulator ignoring <c>$select</c> entirely would
/// still satisfy every typed assertion. The dictionary carries only the keys actually
/// present in the response, which is the thing under test.
/// </remarks>
public class SelectFieldIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task Select_TopLevelFields_ReturnsOnlyThose()
    {
        const string indexName = "test-select-top-level";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Id", "Name"]);

        Assert.Equal(["Id", "Name"], Keys(doc));
        Assert.Equal("Seattle Downtown", doc["Name"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_OmitsFieldsNotAskedFor()
    {
        const string indexName = "test-select-omits";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Name"]);

        // The whole point of the issue: before this, Address and Rooms came back regardless.
        Assert.False(doc.ContainsKey("Address"));
        Assert.False(doc.ContainsKey("Rooms"));
        Assert.False(doc.ContainsKey("Id"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoSelect_ReturnsAllRetrievableFields()
    {
        const string indexName = "test-select-absent";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", select: null);

        Assert.Equal(["Address", "Id", "Name", "Rooms"], Keys(doc));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_ComplexFieldByName_ReturnsWholeObject()
    {
        const string indexName = "test-select-complex-whole";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Address"]);

        Assert.Equal(["Address"], Keys(doc));

        var address = Assert.IsType<SearchDocument>(doc["Address"]);
        Assert.Equal(["City", "Geo", "PostalCode", "Street"], Keys(address));

        // Nesting survives when the parent is taken whole.
        var geo = Assert.IsType<SearchDocument>(address["Geo"]);
        Assert.Equal(47.6062, Convert.ToDouble(geo["Lat"]), 4);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_ComplexSubFieldPath_ReturnsOnlyThatSubField()
    {
        const string indexName = "test-select-complex-subfield";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Address/City"]);

        Assert.Equal(["Address"], Keys(doc));

        var address = Assert.IsType<SearchDocument>(doc["Address"]);
        Assert.Equal(["City"], Keys(address));
        Assert.Equal("Seattle", address["City"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_NestedComplexSubFieldPath_ReturnsOnlyThatLeaf()
    {
        const string indexName = "test-select-complex-nested";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Two levels deep, so this fails unless the selection narrows at every level.
        var doc = await SearchOneAsync(searchClient, "seattle", ["Address/Geo/Lat"]);

        var address = Assert.IsType<SearchDocument>(doc["Address"]);
        Assert.Equal(["Geo"], Keys(address));

        var geo = Assert.IsType<SearchDocument>(address["Geo"]);
        Assert.Equal(["Lat"], Keys(geo));
        Assert.Equal(47.6062, Convert.ToDouble(geo["Lat"]), 4);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_SubFieldOfComplexCollection_NarrowsEveryElement()
    {
        const string indexName = "test-select-complex-collection";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Rooms/Type"]);

        var rooms = Elements(doc, "Rooms");
        Assert.Equal(2, rooms.Count);

        // Every element is narrowed the same way, and the elements stay distinct.
        Assert.All(rooms, r => Assert.Equal(["Type"], Keys(r)));
        Assert.Equal(["Deluxe", "Standard"], rooms.Select(r => (string)r["Type"]));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_MixedTopLevelAndSubFieldPaths_ReturnsBoth()
    {
        const string indexName = "test-select-mixed";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var doc = await SearchOneAsync(searchClient, "seattle", ["Id", "Address/PostalCode", "Rooms/BaseRate"]);

        Assert.Equal(["Address", "Id", "Rooms"], Keys(doc));
        Assert.Equal(["PostalCode"], Keys(Assert.IsType<SearchDocument>(doc["Address"])));
        Assert.All(Elements(doc, "Rooms"), r => Assert.Equal(["BaseRate"], Keys(r)));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_AppliesToEveryResultInThePage()
    {
        const string indexName = "test-select-all-results";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        var options = new SearchOptions { Size = 50 };
        options.Select.Add("Id");

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var docs = (await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document)
            .ToList();

        Assert.Equal(5, docs.Count);
        Assert.All(docs, d => Assert.Equal(["Id"], Keys(d)));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_CombinedWithFilter_ProjectsTheFilteredResults()
    {
        const string indexName = "test-select-with-filter";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // Filtering on a sub-field that is not selected must still work: $select controls
        // what comes back, not what can be queried.
        var options = new SearchOptions { Filter = "Address/City eq 'Seattle'", Size = 50 };
        options.Select.Add("Id");

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var docs = (await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken))
            .Select(r => r.Document)
            .ToList();

        Assert.Equal(["empty", "seattle"], docs.Select(d => (string)d["Id"]).Order(StringComparer.Ordinal));
        Assert.All(docs, d => Assert.Equal(["Id"], Keys(d)));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_OnDocumentLookup_ReturnsOnlySelectedFields()
    {
        const string indexName = "test-select-lookup";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // The Lookup API takes $select too, and shares its projection with search.
        var doc = await searchClient.GetDocumentAsync<SearchDocument>(
            "seattle",
            new GetDocumentOptions { SelectedFields = { "Name", "Address/City" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(["Address", "Name"], Keys(doc.Value));
        Assert.Equal(["City"], Keys(Assert.IsType<SearchDocument>(doc.Value["Address"])));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Select_NullComplexValue_StaysAbsent()
    {
        const string indexName = "test-select-null-complex";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateHotelIndexAsync(indexClient, indexName);
        await UploadHotelsAsync(searchClient);

        // "noaddress" has no Address at all, so selecting into it yields nothing rather
        // than an empty object.
        var doc = await SearchOneAsync(searchClient, "noaddress", ["Id", "Address/City"]);

        Assert.Equal(["Id"], Keys(doc));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sorted field names of a response document, so assertions do not depend on the order
    /// the emulator happens to write them in.
    /// </summary>
    private static IEnumerable<string> Keys(SearchDocument doc)
        => doc.Keys.Where(k => !k.StartsWith('@')).Order();

    /// <summary>
    /// The elements of a complex collection field, each as its own untyped document.
    /// </summary>
    private static List<SearchDocument> Elements(SearchDocument doc, string fieldName)
        => Assert.IsType<object[]>(doc[fieldName], exactMatch: false)
            .Select(e => Assert.IsType<SearchDocument>(e))
            .ToList();

    /// <summary>
    /// Runs a search narrowed to a single hotel, returning it as the untyped document the
    /// server actually sent.
    /// </summary>
    private static async Task<SearchDocument> SearchOneAsync(
        SearchClient searchClient,
        string id,
        IEnumerable<string>? select = null)
    {
        var options = new SearchOptions { Filter = $"Id eq '{id}'", Size = 50 };

        foreach (var field in select ?? [])
        {
            options.Select.Add(field);
        }

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        return Assert.Single(results).Document;
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
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("Name") { IsFilterable = true },
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
                                new SimpleField("Lat", SearchFieldDataType.Double) { IsFilterable = true },
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
                ],
            },
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
            // No address at all, covering a null complex value under projection.
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
