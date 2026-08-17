using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for suggesters and autocomplete (issue #45), run against a containerized
/// emulator through the Azure Search SDK.
/// </summary>
/// <remarks>
/// Going through <see cref="SearchClient.SuggestAsync{T}"/> and
/// <see cref="SearchClient.AutocompleteAsync"/> rather than raw HTTP is the point of these.
/// Both are first-class SDK methods with their own wire contracts — a suggestion carries its
/// text under <c>@search.text</c> alongside the document's fields, and a completion is a
/// <c>text</c>/<c>queryPlusText</c> pair — and the SDK only surfaces them if the emulator
/// emits exactly that shape. A response that were merely plausible would deserialize into
/// empty or wrongly-typed results here.
///
/// The suggester definition also has to survive being written to the index and read back,
/// which is what makes these more than a re-run of the unit tests.
/// </remarks>
public class SuggestAndAutocompleteIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task Suggest_PartialTerm_ReturnsSuggestionsWithTextAndDocument()
    {
        const string indexName = "test-suggest-basic";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var response = await searchClient.SuggestAsync<SuggestProduct>(
            "lap", SuggesterName, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Value.Results.Count);

        Assert.All(response.Value.Results, i =>
        {
            Assert.Contains("Laptop", i.Text);
            // The document travels with the suggestion, not just its text.
            Assert.NotNull(i.Document.Id);
        });

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Suggest_SuggesterDefinitionSurvivesTheIndexRoundTrip()
    {
        const string indexName = "test-suggest-roundtrip";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateProductIndexAsync(indexClient, indexName);

        var index = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        var suggester = Assert.Single(index.Value.Suggesters);
        Assert.Equal(SuggesterName, suggester.Name);
        Assert.Equal(["Name", "Description"], suggester.SourceFields.ToArray());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Suggest_Options_NarrowAndOrderTheSuggestions()
    {
        const string indexName = "test-suggest-options";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var options = new SuggestOptions
        {
            Filter = "Price lt 1000",
            Size = 1,
            HighlightPreTag = "<b>",
            HighlightPostTag = "</b>",
        };
        options.Select.Add("Id");
        options.OrderBy.Add("Price asc");

        var response = await searchClient.SuggestAsync<SuggestProduct>(
            "lap", SuggesterName, options, TestContext.Current.CancellationToken);

        var result = Assert.Single(response.Value.Results);
        // Only the typed prefix is wrapped, not the whole word it landed in.
        Assert.Equal("<b>Lap</b>top Budget 13", result.Text);
        Assert.Equal("2", result.Document.Id);
        // $select narrowed the document to its key.
        Assert.Null(result.Document.Name);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Suggest_MinimumCoverage_IsReportedBack()
    {
        const string indexName = "test-suggest-coverage";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var withCoverage = await searchClient.SuggestAsync<SuggestProduct>(
            "lap", SuggesterName, new SuggestOptions { MinimumCoverage = 50 },
            TestContext.Current.CancellationToken);

        Assert.Equal(100.0, withCoverage.Value.Coverage);

        // Azure omits @search.coverage unless it was asked for, and the SDK surfaces that
        // absence as null; emitting it unconditionally would make a caller's null check
        // unreachable locally while it still fires against the real service.
        var withoutCoverage = await searchClient.SuggestAsync<SuggestProduct>(
            "lap", SuggesterName, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(withoutCoverage.Value.Coverage);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Suggest_UnknownSuggester_IsRejected()
    {
        const string indexName = "test-suggest-unknown";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(() =>
            searchClient.SuggestAsync<SuggestProduct>(
                "lap", "no-such-suggester", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(400, ex.Status);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("oneTerm", "laptop")]
    [InlineData("twoTerms", "laptop budget")]
    public async Task Autocomplete_Mode_CompletesTheTypedTerm(string mode, string expected)
    {
        var indexName = $"test-autocomplete-{mode.ToLowerInvariant()}";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var options = new AutocompleteOptions
        {
            Mode = mode == "oneTerm" ? AutocompleteMode.OneTerm : AutocompleteMode.TwoTerms,
            Filter = "Price lt 1000",
        };

        var response = await searchClient.AutocompleteAsync(
            "lap", SuggesterName, options, TestContext.Current.CancellationToken);

        // The filter leaves only the budget laptop, whose Name and Description both contain
        // the term — so twoTerms legitimately completes it from each, while oneTerm collapses
        // both to the single word.
        Assert.Contains(response.Value.Results, i => i.Text == expected);
        Assert.All(response.Value.Results, i =>
        {
            Assert.StartsWith("laptop", i.Text);
            // With nothing typed before the partial term, the completion stands on its own.
            Assert.Equal(i.Text, i.QueryPlusText);
        });

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Autocomplete_OneTermWithContext_RequiresThePrecedingWordToMatch()
    {
        const string indexName = "test-autocomplete-context";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        var options = new AutocompleteOptions { Mode = AutocompleteMode.OneTermWithContext };

        // "Affordable laptop" occurs in a Description; "gaming laptop" occurs nowhere.
        var matching = await searchClient.AutocompleteAsync(
            "affordable lap", SuggesterName, options, TestContext.Current.CancellationToken);

        var item = Assert.Single(matching.Value.Results);
        Assert.Equal("laptop", item.Text);
        // The completed terms come back as the caller typed them.
        Assert.Equal("affordable laptop", item.QueryPlusText);

        var nonMatching = await searchClient.AutocompleteAsync(
            "gaming lap", SuggesterName, options, TestContext.Current.CancellationToken);

        Assert.Empty(nonMatching.Value.Results);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Autocomplete_ReturnsDistinctCompletionsAcrossDocuments()
    {
        const string indexName = "test-autocomplete-distinct";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateProductIndexAsync(indexClient, indexName);
        await UploadProductsAsync(searchClient);

        // "laptop" occurs in several documents' fields, but is one completion.
        var response = await searchClient.AutocompleteAsync(
            "lapt", SuggesterName, cancellationToken: TestContext.Current.CancellationToken);

        var item = Assert.Single(response.Value.Results);
        Assert.Equal("laptop", item.Text);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    private const string SuggesterName = "sg";

    private static async Task CreateProductIndexAsync(SearchIndexClient indexClient, string indexName)
    {
        var index = new SearchIndex(indexName)
        {
            Fields =
            {
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true },
                new SearchableField("Name") { IsFilterable = true },
                new SearchableField("Description"),
                new SimpleField("Price", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
            },
            Suggesters =
            {
                new SearchSuggester(SuggesterName, "Name", "Description"),
            },
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    private static async Task UploadProductsAsync(SearchClient searchClient)
    {
        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload<SuggestProduct>(
            [
                new SuggestProduct { Id = "1", Name = "Laptop Pro 15", Description = "High performance laptop computer", Price = 1299.99 },
                new SuggestProduct { Id = "2", Name = "Laptop Budget 13", Description = "Affordable laptop for students", Price = 499.99 },
                new SuggestProduct { Id = "3", Name = "Gaming Mouse", Description = "Precision gaming mouse", Price = 59.99 },
                new SuggestProduct { Id = "4", Name = "Mechanical Keyboard", Description = "Keyboard with lamp indicator", Price = 149.99 },
            ]),
            cancellationToken: TestContext.Current.CancellationToken);

        await WaitForCountAsync(searchClient, 4);
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

    /// <summary>
    /// Every property is nullable so that a <c>$select</c> narrowing the response to the key
    /// can be asserted on, which a model with required properties could not express.
    /// </summary>
    private class SuggestProduct
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public double? Price { get; set; }
    }
}
