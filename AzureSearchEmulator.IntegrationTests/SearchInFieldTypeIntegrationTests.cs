using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for issue #72 — <c>search.in</c> over fields that are not strings — run
/// against a containerized emulator through the Azure Search SDK.
/// </summary>
/// <remarks>
/// Each test compares <c>search.in</c> against the <c>or</c> chain of <c>eq</c> comparisons it
/// is documented shorthand for. That equivalence is the property the bug broke: the filter
/// returned an empty set with no error, so a test asserting only "some ids came back" from one
/// side would not have caught it.
/// </remarks>
public class SearchInFieldTypeIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private static readonly DateTimeOffset Older = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Middle = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Newer = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The case from the issue: an Int32 filter that returned nothing while the identical
    /// or-chain returned two documents.
    /// </summary>
    [Fact]
    public async Task SearchIn_OnInt32Field_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-int32",
            "search.in(Rating, '4,5')",
            "Rating eq 4 or Rating eq 5",
            ["alpha", "bravo"]);
    }

    [Fact]
    public async Task SearchIn_OnInt64Field_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-int64",
            "search.in(Views, '4000000000,6000000000')",
            "Views eq 4000000000 or Views eq 6000000000",
            ["alpha", "charlie"]);
    }

    [Fact]
    public async Task SearchIn_OnDoubleField_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-double",
            "search.in(Price, '9.5,29.5')",
            "Price eq 9.5 or Price eq 29.5",
            ["alpha", "charlie"]);
    }

    [Fact]
    public async Task SearchIn_OnBooleanField_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-boolean",
            "search.in(InStock, 'true')",
            "InStock eq true",
            ["alpha", "charlie"]);
    }

    [Fact]
    public async Task SearchIn_OnDateTimeOffsetField_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-date",
            "search.in(Updated, '2024-01-01T00:00:00Z,2025-01-01T00:00:00Z')",
            "Updated eq 2024-01-01T00:00:00Z or Updated eq 2025-01-01T00:00:00Z",
            ["alpha", "charlie"]);
    }

    /// <summary>
    /// A numeric collection is indexed one value per element, so membership has to be read
    /// against the element type rather than the declared Collection(...) type.
    /// </summary>
    [Fact]
    public async Task SearchIn_OnInt32CollectionField_AgreesWithAnyChain()
    {
        await RunAsync(
            "test-search-in-int32-collection",
            "search.in(Sizes, '1,4')",
            "Sizes/any(s: s eq 1) or Sizes/any(s: s eq 4)",
            ["alpha", "charlie"]);
    }

    /// <summary>
    /// The string case already worked; it is here so a regression in the shared path shows up
    /// as a failure rather than as silently narrower coverage.
    /// </summary>
    [Fact]
    public async Task SearchIn_OnStringField_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-string",
            "search.in(Category, 'a,c')",
            "Category eq 'a' or Category eq 'c'",
            ["alpha", "charlie"]);
    }

    [Fact]
    public async Task SearchIn_WithCustomDelimiterOnNumericField_AgreesWithOrChain()
    {
        await RunAsync(
            "test-search-in-delimiter",
            "search.in(Rating, '4|5', '|')",
            "Rating eq 4 or Rating eq 5",
            ["alpha", "bravo"]);
    }

    /// <summary>
    /// Combining search.in with another predicate has to narrow the result rather than
    /// collapse it, which is where a filter that silently matches nothing does the most damage.
    /// </summary>
    [Fact]
    public async Task SearchIn_CombinedWithAnotherPredicate_NarrowsTheResult()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-search-in-combined");

        Assert.Equal(
            ["alpha"],
            await FilterIdsAsync(searchClient, "search.in(Rating, '4,5') and InStock eq true"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// No document holds these ratings, so the empty result here is the correct answer rather
    /// than the bug's answer — the other tests are what tell the two apart.
    /// </summary>
    [Fact]
    public async Task SearchIn_WithNoMatchingValues_ReturnsNothing()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-search-in-no-match");

        Assert.Empty(await FilterIdsAsync(searchClient, "search.in(Rating, '99,100')"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// search.in previously skipped the filterable check every other comparison runs, so a
    /// non-filterable field silently returned nothing instead of reporting the mistake.
    /// </summary>
    [Fact]
    public async Task SearchIn_OnNonFilterableField_ReturnsAnError()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-search-in-not-filterable");

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => FilterIdsAsync(searchClient, "search.in(Description, 'x')"));

        Assert.Equal(400, ex.Status);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    private async Task RunAsync(string indexName, string searchInFilter, string orChainFilter, string[] expectedIds)
    {
        var (indexClient, searchClient, resolvedName) = await SetUpAsync(indexName);

        var searchInIds = await FilterIdsAsync(searchClient, searchInFilter);

        Assert.Equal(expectedIds, searchInIds);
        Assert.Equal(await FilterIdsAsync(searchClient, orChainFilter), searchInIds);

        await indexClient.DeleteIndexAsync(resolvedName, TestContext.Current.CancellationToken);
    }

    private async Task<(SearchIndexClient IndexClient, SearchClient SearchClient, string IndexName)> SetUpAsync(
        string indexName)
    {
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

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
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("Rating", SearchFieldDataType.Int32) { IsFilterable = true },
                new SimpleField("Views", SearchFieldDataType.Int64) { IsFilterable = true },
                new SimpleField("Price", SearchFieldDataType.Double) { IsFilterable = true },
                new SimpleField("InStock", SearchFieldDataType.Boolean) { IsFilterable = true },
                new SimpleField("Updated", SearchFieldDataType.DateTimeOffset) { IsFilterable = true },
                new SimpleField("Sizes", SearchFieldDataType.Collection(SearchFieldDataType.Int32)) { IsFilterable = true },
                // Searchable but not filterable, so the filterable check has something to reject.
                new SearchableField("Description"),
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var documents = new List<SearchDocument>
        {
            Doc("alpha", "a", 4, 4_000_000_000L, 9.5, true, Older, [1, 2]),
            Doc("bravo", "b", 5, 5_000_000_000L, 19.5, false, Middle, [2, 3]),
            Doc("charlie", "c", 3, 6_000_000_000L, 29.5, true, Newer, [3, 4]),
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        return (indexClient, searchClient, indexName);
    }

    private static SearchDocument Doc(
        string id,
        string category,
        int rating,
        long views,
        double price,
        bool inStock,
        DateTimeOffset updated,
        int[] sizes) => new()
    {
        ["Id"] = id,
        ["Category"] = category,
        ["Rating"] = rating,
        ["Views"] = views,
        ["Price"] = price,
        ["InStock"] = inStock,
        ["Updated"] = updated,
        ["Sizes"] = sizes,
        ["Description"] = "a widget for testing",
    };

    /// <summary>
    /// Ids matching a filter, sorted so assertions do not depend on result ordering.
    /// </summary>
    private static async Task<List<string>> FilterIdsAsync(SearchClient searchClient, string filter)
    {
        var options = new SearchOptions { Filter = filter, Size = 50 };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        return results
            .Select(r => (string)r.Document["Id"])
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
