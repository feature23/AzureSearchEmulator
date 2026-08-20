using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for synonym map support (issue #69), run against a containerized emulator
/// through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// The SDK is the point of these tests. It decides the routes a synonym map is created and read
/// on, the <c>value</c> wrapper a listing comes back in, and the <c>format</c>/<c>synonyms</c>
/// property names that go on the wire — none of which the unit tests exercise, because they
/// call the emulator's own types directly. A map the emulator stored under a different shape,
/// or a <c>synonymMaps</c> field property it accepted and ignored, would fail here rather than
/// pass unnoticed.
///
/// The scenarios are the ones Azure's documentation gives: a query for a word the indexed
/// documents do not contain, matched through a rule.
/// </remarks>
public class SynonymMapIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private class Document
    {
        public string Id { get; set; } = "";

        public string? Name { get; set; }
    }

    private static readonly Document[] Products =
    [
        new() { Id = "1", Name = "canine chew toy" },
        new() { Id = "2", Name = "feline scratcher" },
        new() { Id = "3", Name = "united states map" },
    ];

    private static SearchIndex CreateIndex(string indexName, string? synonymMapName)
    {
        var name = new SearchField(nameof(Document.Name), SearchFieldDataType.String)
        {
            IsSearchable = true,
            IsStored = true,
        };

        if (synonymMapName != null)
        {
            name.SynonymMapNames.Add(synonymMapName);
        }

        return new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                name,
            }
        };
    }

    private static async Task<SearchClient> PopulateAsync(SearchIndexClient indexClient, SearchIndex index)
    {
        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var searchClient = indexClient.GetSearchClient(index.Name);

        var response = await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(Products),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(response.Value.Results, i => Assert.True(i.Succeeded));

        await WaitForCountAsync(searchClient, Products.Length);

        return searchClient;
    }

    private static async Task<List<string>> IdsAsync(SearchClient searchClient, string search)
    {
        var response = await searchClient.SearchAsync<Document>(
            search, new SearchOptions(), TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        return ids;
    }

    /// <summary>
    /// A synonym map survives being written and read back through the SDK, with its rules
    /// intact.
    /// </summary>
    /// <remarks>
    /// Read back before any search, so a persistence fault shows as a definition mismatch here
    /// rather than as a mysteriously empty result later.
    /// </remarks>
    [Fact]
    public async Task SynonymMap_RoundTrips()
    {
        const string mapName = "test-synonyms-roundtrip";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "usa, united states\ndog => canine"),
            TestContext.Current.CancellationToken);

        var read = await indexClient.GetSynonymMapAsync(mapName, TestContext.Current.CancellationToken);

        Assert.Equal(mapName, read.Value.Name);
        Assert.Contains("usa, united states", read.Value.Synonyms);
        Assert.Contains("dog => canine", read.Value.Synonyms);

        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A created map appears in the listing, which the SDK reads out of the <c>value</c> wrapper.
    /// </summary>
    [Fact]
    public async Task SynonymMaps_AreListed()
    {
        const string mapName = "test-synonyms-listed";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "usa, united states"),
            TestContext.Current.CancellationToken);

        var names = await indexClient.GetSynonymMapNamesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(mapName, names.Value);

        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A deleted map is gone.
    /// </summary>
    [Fact]
    public async Task DeletedSynonymMap_IsNotFound()
    {
        const string mapName = "test-synonyms-deleted";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "usa, united states"),
            TestContext.Current.CancellationToken);

        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.GetSynonymMapAsync(mapName, TestContext.Current.CancellationToken));

        Assert.Equal(404, ex.Status);
    }

    /// <summary>
    /// An edit replaces the rules, and the map reads back with the new ones.
    /// </summary>
    [Fact]
    public async Task SynonymMap_CanBeEdited()
    {
        const string mapName = "test-synonyms-edited";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "usa, united states"),
            TestContext.Current.CancellationToken);

        await indexClient.CreateOrUpdateSynonymMapAsync(
            new SynonymMap(mapName, "dog, canine"),
            onlyIfUnchanged: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var read = await indexClient.GetSynonymMapAsync(mapName, TestContext.Current.CancellationToken);

        Assert.Equal("dog, canine", read.Value.Synonyms);

        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The scenario the feature exists for: a query for a word no document contains matches
    /// through an equivalency rule.
    /// </summary>
    [Fact]
    public async Task EquivalencyRule_WidensTheQuery()
    {
        const string indexName = "test-synonym-equivalency";
        const string mapName = "test-synonyms-equivalency";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "dog, canine"),
            TestContext.Current.CancellationToken);

        var searchClient = await PopulateAsync(indexClient, CreateIndex(indexName, mapName));

        Assert.Equal(["1"], await IdsAsync(searchClient, "dog"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sits beside the test above: the same query against a field naming no map matches
    /// nothing, so the match there comes from the expansion rather than from looser matching.
    /// </summary>
    [Fact]
    public async Task WithoutASynonymMap_TheSameQueryMatchesNothing()
    {
        const string indexName = "test-synonym-none";

        var indexClient = factory.CreateSearchIndexClient();

        var searchClient = await PopulateAsync(indexClient, CreateIndex(indexName, synonymMapName: null));

        Assert.Empty(await IdsAsync(searchClient, "dog"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A mapping rule replaces the query's term rather than adding to it.
    /// </summary>
    [Fact]
    public async Task MappingRule_SearchesForTheReplacement()
    {
        const string indexName = "test-synonym-mapping";
        const string mapName = "test-synonyms-mapping";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "cat => feline"),
            TestContext.Current.CancellationToken);

        var searchClient = await PopulateAsync(indexClient, CreateIndex(indexName, mapName));

        Assert.Equal(["2"], await IdsAsync(searchClient, "cat"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A multi-word rule matches as a phrase.
    /// </summary>
    [Fact]
    public async Task MultiWordRule_MatchesThePhrase()
    {
        const string indexName = "test-synonym-phrase";
        const string mapName = "test-synonyms-phrase";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "usa, united states"),
            TestContext.Current.CancellationToken);

        var searchClient = await PopulateAsync(indexClient, CreateIndex(indexName, mapName));

        Assert.Equal(["3"], await IdsAsync(searchClient, "usa"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Editing a map changes what a query matches without the documents being touched, which is
    /// the property that makes a map safe to edit.
    /// </summary>
    [Fact]
    public async Task EditedSynonymMap_TakesEffectWithoutReindexing()
    {
        const string indexName = "test-synonym-reedit";
        const string mapName = "test-synonyms-reedit";

        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateSynonymMapAsync(
            new SynonymMap(mapName, "bird, avian"),
            TestContext.Current.CancellationToken);

        var searchClient = await PopulateAsync(indexClient, CreateIndex(indexName, mapName));

        Assert.Empty(await IdsAsync(searchClient, "dog"));

        await indexClient.CreateOrUpdateSynonymMapAsync(
            new SynonymMap(mapName, "dog, canine"),
            onlyIfUnchanged: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["1"], await IdsAsync(searchClient, "dog"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        await indexClient.DeleteSynonymMapAsync(mapName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An index naming a synonym map that does not exist is refused when it is created, rather
    /// than accepted and left to search unexpanded.
    /// </summary>
    [Fact]
    public async Task IndexNamingAMissingSynonymMap_IsRejected()
    {
        const string indexName = "test-synonym-missing";

        var indexClient = factory.CreateSearchIndexClient();

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            indexClient.CreateIndexAsync(
                CreateIndex(indexName, "no-such-map"),
                TestContext.Current.CancellationToken));

        Assert.Equal(400, ex.Status);
    }

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

        Assert.Fail($"{expected} documents were expected to become searchable, but the count never reached it.");
    }
}
