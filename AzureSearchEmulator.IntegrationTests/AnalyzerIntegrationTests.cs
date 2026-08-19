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
/// Integration tests for analyzer support (issue #34), run against a containerized emulator
/// through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// The SDK is the point of these tests, as it is for the vector search ones. It models
/// <c>CustomAnalyzer</c>, the tokenizer and filter definitions, and the
/// <c>LexicalAnalyzerName</c> constants as strongly-typed classes, and it decides the property
/// names and <c>@odata.type</c> discriminators that go on the wire. An analyzer definition the
/// emulator read under a different name would fail here rather than pass unnoticed.
///
/// The scenario in the issue is the one at the top: an index whose fields use
/// <c>en.microsoft</c> could be created but not indexed into, because the analyzer lookup threw.
/// </remarks>
public class AnalyzerIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private class Document
    {
        public string Id { get; set; } = "";

        public string? Title { get; set; }
    }

    /// <summary>
    /// The failure reported in issue #34: an index using a Microsoft language analyzer accepted
    /// the definition, then threw on the first document indexed into it.
    /// </summary>
    [Theory]
    [InlineData("en.microsoft")]
    [InlineData("de.microsoft")]
    [InlineData("ja.microsoft")]
    [InlineData("pt-BR.lucene")]
    public async Task FieldUsingPreviouslyUnsupportedAnalyzer_CanBeIndexedAndSearched(string analyzerName)
    {
        var indexName = $"test-analyzer-{analyzerName.Replace('.', '-').ToLowerInvariant()}";
        var indexClient = factory.CreateSearchIndexClient();

        await indexClient.CreateIndexAsync(
            new SearchIndex(indexName)
            {
                Fields =
                {
                    new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                        { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                    new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                        { IsSearchable = true, IsStored = true, AnalyzerName = analyzerName },
                }
            },
            TestContext.Current.CancellationToken);

        var searchClient = factory.CreateSearchClient(indexName);

        var response = await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", Title = "hello world" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Value.Results[0].Succeeded);

        await WaitForCountAsync(searchClient, 1);

        var results = await searchClient.SearchAsync<Document>(
            "hello",
            cancellationToken: TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        Assert.Equal(["1"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A custom analyzer defined through the SDK survives the round-trip and actually governs
    /// how the field is tokenized.
    /// </summary>
    [Fact]
    public async Task CustomAnalyzer_RoundTripsAndGovernsMatching()
    {
        const string indexName = "test-analyzer-custom";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true, AnalyzerName = "myAnalyzer" },
            },
            Analyzers =
            {
                new CustomAnalyzer("myAnalyzer", LexicalTokenizerName.Standard)
                {
                    TokenFilters = { TokenFilterName.Lowercase, TokenFilterName.AsciiFolding },
                    CharFilters = { CharFilterName.HtmlStrip.ToString() },
                }
            }
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var stored = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        var analyzer = Assert.IsType<CustomAnalyzer>(Assert.Single(stored.Value.Analyzers));
        Assert.Equal("myAnalyzer", analyzer.Name);
        Assert.Equal(LexicalTokenizerName.Standard, analyzer.TokenizerName);
        Assert.Equal([TokenFilterName.Lowercase, TokenFilterName.AsciiFolding], analyzer.TokenFilters);
        Assert.Equal([CharFilterName.HtmlStrip.ToString()], analyzer.CharFilters);

        var searchClient = factory.CreateSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", Title = "<b>CAFÉ</b> Noir" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 1);

        // The chain is what makes this match: html_strip removed the markup, lowercase and
        // asciifolding turned "CAFÉ" into "cafe". None of those hold under the default analyzer,
        // which leaves the accent in place.
        var results = await searchClient.SearchAsync<Document>(
            "cafe",
            cancellationToken: TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        Assert.Equal(["1"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A tokenizer definition's options have to reach the field, not just survive the round-trip.
    /// </summary>
    [Fact]
    public async Task DefinedTokenizer_AppliesItsOptions()
    {
        const string indexName = "test-analyzer-tokenizer-options";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true, AnalyzerName = "grams" },
            },
            Analyzers = { new CustomAnalyzer("grams", "gram3") },
            Tokenizers = { new NGramTokenizer("gram3") { MinGram = 3, MaxGram = 3 } }
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var stored = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        var tokenizer = Assert.IsType<NGramTokenizer>(Assert.Single(stored.Value.Tokenizers));
        Assert.Equal(3, tokenizer.MinGram);
        Assert.Equal(3, tokenizer.MaxGram);

        var searchClient = factory.CreateSearchClient(indexName);

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(new[]
            {
                new Document { Id = "1", Title = "abcde" },
            }),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 1);

        // "bcd" is an interior trigram: it matches only because the field was tokenized into
        // 3-grams, which is what the definition asked for.
        var results = await searchClient.SearchAsync<Document>(
            "bcd",
            cancellationToken: TestContext.Current.CancellationToken);

        var ids = new List<string>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            ids.Add(result.Document.Id);
        }

        Assert.Equal(["1"], ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An unknown analyzer is refused when the index is created, rather than being accepted and
    /// failing later on the first document — which is where issue #34 found it.
    /// </summary>
    [Fact]
    public async Task FieldNamingUnknownAnalyzer_IsRejectedAtIndexCreation()
    {
        const string indexName = "test-analyzer-unknown";
        var indexClient = factory.CreateSearchIndexClient();

        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SearchField(nameof(Document.Id), SearchFieldDataType.String)
                    { IsKey = true, IsSearchable = true, IsFilterable = true, IsStored = true },
                new SearchField(nameof(Document.Title), SearchFieldDataType.String)
                    { IsSearchable = true, IsStored = true, AnalyzerName = "no.such.analyzer" },
            }
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);

        // The message has to name the analyzer and the field, which is what the original
        // messageless NotSupportedException could not do.
        Assert.Contains("no.such.analyzer", ex.Message);
        Assert.Contains(nameof(Document.Title), ex.Message);
    }

    /// <summary>
    /// Waits until the indexed documents are visible to search.
    /// </summary>
    /// <remarks>
    /// Indexing commits before returning, but the searcher's reader is refreshed independently,
    /// so a search issued immediately afterwards can still observe the previous state.
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

        Assert.Fail($"{expected} documents were expected to become searchable, but the count never reached it.");
    }
}
