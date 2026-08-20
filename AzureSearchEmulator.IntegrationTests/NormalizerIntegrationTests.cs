using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for normalizer support (issue #74), run against a containerized emulator
/// through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// The SDK is the point of these tests, as it is for the analyzer ones. It models
/// <c>CustomNormalizer</c> and the <c>LexicalNormalizerName</c> constants as strongly-typed
/// classes, and it decides the property names and the <c>@odata.type</c> discriminator that go
/// on the wire. A definition the emulator read under a different name, or a <c>normalizer</c>
/// property it accepted and ignored — which is what it did before this — would fail here rather
/// than pass unnoticed.
///
/// The scenarios are the ones Azure's documentation opens with: variants of "Las Vegas" that a
/// filter, a facet and a sort should each treat as one value.
/// </remarks>
public class NormalizerIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private class Document
    {
        public string Id { get; set; } = "";

        public string? City { get; set; }
    }

    private static readonly Document[] Cities =
    [
        new() { Id = "1", City = "Las Vegas" },
        new() { Id = "2", City = "LAS VEGAS" },
        new() { Id = "3", City = "las vegas" },
        new() { Id = "4", City = "Seattle" },
    ];

    private static SearchIndex CreateIndex(string indexName, string normalizerName) =>
        new(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.City), SearchFieldDataType.String)
                {
                    IsSearchable = true,
                    IsFilterable = true,
                    IsFacetable = true,
                    IsSortable = true,
                    IsStored = true,
                    NormalizerName = normalizerName,
                },
            }
        };

    private static async Task<SearchClient> PopulateAsync(SearchIndexClient indexClient, SearchIndex index)
    {
        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var searchClient = indexClient.GetSearchClient(index.Name);

        var response = await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(Cities),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(response.Value.Results, i => Assert.True(i.Succeeded));

        await WaitForCountAsync(searchClient, Cities.Length);

        return searchClient;
    }

    private static async Task<List<string>> IdsAsync(SearchClient searchClient, SearchOptions options)
    {
        var results = await searchClient.SearchAsync<Document>(
            "*", options, TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        return ids;
    }

    /// <summary>
    /// The scenario the Azure documentation opens with, end to end: a filter that would
    /// otherwise match only one casing matches every variant.
    /// </summary>
    /// <remarks>
    /// This is what issue #74 reported. The <c>normalizer</c> property was accepted and then
    /// ignored, so this filter returned the single exactly-matching document.
    /// </remarks>
    [Fact]
    public async Task Filter_MatchesEveryCasingOfTheValue()
    {
        const string indexName = "test-normalizer-filter";
        var indexClient = factory.CreateSearchIndexClient();

        var searchClient = await PopulateAsync(
            indexClient, CreateIndex(indexName, LexicalNormalizerName.Lowercase.ToString()));

        var ids = await IdsAsync(searchClient, new SearchOptions { Filter = "City eq 'las vegas'" });

        Assert.Equal(["1", "2", "3"], ids.Order());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Facets count the folded value, so the three spellings form one bucket rather than three.
    /// </summary>
    [Fact]
    public async Task Facet_CountsNormalizedValuesAsOneBucket()
    {
        const string indexName = "test-normalizer-facet";
        var indexClient = factory.CreateSearchIndexClient();

        var searchClient = await PopulateAsync(
            indexClient, CreateIndex(indexName, LexicalNormalizerName.Lowercase.ToString()));

        var results = await searchClient.SearchAsync<Document>(
            "*",
            new SearchOptions { Facets = { "City" } },
            TestContext.Current.CancellationToken);

        var facets = results.Value.Facets["City"]
            .Select(i => ($"{i.Value}", i.Count))
            .OrderBy(i => i.Item1)
            .ToList();

        Assert.Equal([("las vegas", 3L), ("seattle", 1L)], facets);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Sorting orders by the folded value, which is what keeps the variants together.
    /// </summary>
    /// <remarks>
    /// Without a normalizer, Lucene's byte-wise term ordering puts "LAS VEGAS" and "Las Vegas"
    /// before "Seattle" but "las vegas" after it — the interleaving Azure's documentation gives
    /// as the reason sorting needs one.
    /// </remarks>
    [Fact]
    public async Task Sort_OrdersByTheNormalizedValue()
    {
        const string indexName = "test-normalizer-sort";
        var indexClient = factory.CreateSearchIndexClient();

        var searchClient = await PopulateAsync(
            indexClient, CreateIndex(indexName, LexicalNormalizerName.Lowercase.ToString()));

        var ids = await IdsAsync(searchClient, new SearchOptions { OrderBy = { "City asc", "Id asc" } });

        Assert.Equal(["1", "2", "3", "4"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A normalizer changes what a field is compared on, never what it returns.
    /// </summary>
    [Fact]
    public async Task Retrieval_ReturnsTheOriginalValue()
    {
        const string indexName = "test-normalizer-retrieval";
        var indexClient = factory.CreateSearchIndexClient();

        var searchClient = await PopulateAsync(
            indexClient, CreateIndex(indexName, LexicalNormalizerName.Lowercase.ToString()));

        var document = await searchClient.GetDocumentAsync<Document>(
            "2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("LAS VEGAS", document.Value.City);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The standard normalizer folds accents as well as case, on both sides of the comparison.
    /// </summary>
    [Fact]
    public async Task StandardNormalizer_FoldsAccentsAndCase()
    {
        const string indexName = "test-normalizer-standard";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(
            CreateIndex(indexName, LexicalNormalizerName.Standard.ToString()),
            TestContext.Current.CancellationToken);

        var searchClient = indexClient.GetSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", City = "MONTRÉAL" },
                new Document { Id = "2", City = "Montreal" },
                new Document { Id = "3", City = "Seattle" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 3);

        var ids = await IdsAsync(searchClient, new SearchOptions { Filter = "City eq 'montreal'" });

        Assert.Equal(["1", "2"], ids.Order());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A custom normalizer defined through the SDK survives the round-trip and governs what the
    /// filter matches.
    /// </summary>
    /// <remarks>
    /// Uses the example from Azure's own documentation: dashes mapped to underscores by a char
    /// filter, then asciifolding and lowercase applied to the token.
    /// </remarks>
    [Fact]
    public async Task CustomNormalizer_RoundTripsAndGovernsMatching()
    {
        const string indexName = "test-normalizer-custom";
        var indexClient = factory.CreateSearchIndexClient();

        var index = CreateIndex(indexName, "my_custom_normalizer");

        index.Normalizers.Add(new CustomNormalizer("my_custom_normalizer")
        {
            CharFilters = { "map_dash" },
            TokenFilters = { TokenFilterName.AsciiFolding, TokenFilterName.Lowercase },
        });

        index.CharFilters.Add(new MappingCharFilter("map_dash", ["-=>_"]));

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        // Read back before indexing, so a definition the emulator failed to persist shows up
        // as a definition mismatch rather than as a filter that mysteriously matches nothing.
        var stored = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);
        var normalizer = Assert.IsType<CustomNormalizer>(Assert.Single(stored.Value.Normalizers));

        Assert.Equal("my_custom_normalizer", normalizer.Name);
        Assert.Equal(["map_dash"], normalizer.CharFilters);
        Assert.Equal(
            [TokenFilterName.AsciiFolding, TokenFilterName.Lowercase],
            normalizer.TokenFilters);

        var searchClient = indexClient.GetSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", City = "Vis-à-vis" },
                new Document { Id = "2", City = "VIS-A-VIS" },
                new Document { Id = "3", City = "Seattle" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 3);

        // Both documents normalize to "vis_a_vis": the dashes become underscores, the accent
        // is folded and the case is dropped.
        var ids = await IdsAsync(searchClient, new SearchOptions { Filter = "City eq 'Vis-À-Vis'" });

        Assert.Equal(["1", "2"], ids.Order());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
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
