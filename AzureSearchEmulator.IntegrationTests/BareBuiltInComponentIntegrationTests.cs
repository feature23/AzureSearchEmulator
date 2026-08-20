using System.Net;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for naming a built-in analysis component without defining it (issue #73),
/// run against a containerized emulator through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// The scenario in the issue is the first test: an index whose custom analyzer names the
/// <c>pattern</c> tokenizer with no accompanying definition is a valid Azure index definition,
/// and creating it here returned a 400.
///
/// Going through the SDK matters for the same reason it does in
/// <see cref="AnalyzerIntegrationTests"/>: <c>LexicalTokenizerName.Pattern</c> is the constant
/// a caller would actually reach for, and it puts the bare name on the wire with no tokenizer
/// definition beside it — exactly the shape that failed.
/// </remarks>
public class BareBuiltInComponentIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private class Document
    {
        public string Id { get; set; } = "";

        public string? Title { get; set; }
    }

    /// <summary>
    /// The failure reported in issue #73, end to end: create the index, index into it, and
    /// search a term that only the pattern tokenizer's default split produces.
    /// </summary>
    [Fact]
    public async Task BarePatternTokenizer_CanBeCreatedIndexedAndSearched()
    {
        const string indexName = "test-bare-pattern-tokenizer";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true, AnalyzerName = "patternAnalyzer" },
            },

            // The shape from the issue: the tokenizer is named, and nothing defines it.
            Analyzers = { new CustomAnalyzer("patternAnalyzer", LexicalTokenizerName.Pattern) },
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var searchClient = factory.CreateSearchClient(indexName);

        var response = await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", Title = "alpha-beta.gamma" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Value.Results[0].Succeeded);

        await WaitForCountAsync(searchClient, 1);

        // "beta" is only a term of its own because the default \W+ pattern split the value on
        // the hyphen and the dot; an unsplit field would not match it.
        Assert.Equal(["1"], await SearchIdsAsync(searchClient, "beta"));
    }

    /// <summary>
    /// The equivalence the issue asks for, across the API: the bare name and an explicit
    /// <c>\W+</c> definition have to match the same documents.
    /// </summary>
    [Fact]
    public async Task BarePatternTokenizer_MatchesTheSameTermsAsAnExplicitDefinition()
    {
        const string indexName = "test-bare-pattern-equivalent";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },

                // One field analyzed by the bare name, one by the spelled-out equivalent.
                new SearchField("bare", SearchFieldDataType.String)
                    { IsSearchable = true, AnalyzerName = "bareAnalyzer" },
                new SearchField("defined", SearchFieldDataType.String)
                    { IsSearchable = true, AnalyzerName = "definedAnalyzer" },
            },
            Analyzers =
            {
                new CustomAnalyzer("bareAnalyzer", LexicalTokenizerName.Pattern),
                new CustomAnalyzer("definedAnalyzer", "explicitPattern"),
            },
            Tokenizers = { new PatternTokenizer("explicitPattern") { Pattern = @"\W+" } },
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var searchClient = factory.CreateSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new { Id = "1", bare = "alpha-beta.gamma", defined = "alpha-beta.gamma" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 1);

        foreach (var term in new[] { "alpha", "beta", "gamma" })
        {
            Assert.Equal(
                await SearchIdsAsync(searchClient, $"bare:{term}"),
                await SearchIdsAsync(searchClient, $"defined:{term}"));
        }
    }

    /// <summary>
    /// The other two components that could not be named bare, in the shape a caller would
    /// write them.
    /// </summary>
    [Theory]
    [InlineData("length")]
    [InlineData("limit")]
    public async Task BareTokenFilter_CanBeCreatedAndIndexedInto(string filter)
    {
        var indexName = $"test-bare-filter-{filter}";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true, AnalyzerName = "filtered" },
            },
            Analyzers =
            {
                new CustomAnalyzer("filtered", LexicalTokenizerName.Whitespace)
                {
                    TokenFilters = { new TokenFilterName(filter) },
                },
            },
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var response = await factory.CreateSearchClient(indexName).IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", Title = "one two three" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Value.Results[0].Succeeded);
    }

    /// <summary>
    /// A component whose central option Azure marks required stays a 400 — the fix defaults
    /// what Azure defaults, and no more — but the message has to name the missing option
    /// rather than blaming a missing assembly.
    /// </summary>
    [Fact]
    public async Task BareTokenFilterMissingARequiredOption_IsRejectedWithAUsefulMessage()
    {
        const string indexName = "test-bare-filter-required-option";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, AnalyzerName = "needsPattern" },
            },
            Analyzers =
            {
                new CustomAnalyzer("needsPattern", LexicalTokenizerName.Whitespace)
                {
                    TokenFilters = { new TokenFilterName("pattern_replace") },
                },
            },
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("pattern_replace", ex.Message);
        Assert.Contains("pattern", ex.Message);
        Assert.DoesNotContain("Assembly", ex.Message);
    }

    private static async Task<List<string>> SearchIdsAsync(SearchClient searchClient, string query)
    {
        var results = await searchClient.SearchAsync<Document>(
            query,
            cancellationToken: TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        return ids;
    }

    /// <summary>
    /// Waits until the indexed documents are visible to search.
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

        Assert.Fail($"{expected} documents were expected to become searchable, but the count never reached it.");
    }
}
