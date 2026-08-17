using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for the three <c>$filter</c> gaps in issue #44 — null comparison,
/// lexicographic string ranges, and <c>search.ismatch</c> not contributing to scoring — run
/// against a containerized emulator through the Azure Search SDK.
/// </summary>
public class FilterGapIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    // ===== 1. Null comparison =====

    [Fact]
    public async Task Filter_EqNull_ReturnsDocumentsWithNoValue()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-eq-null");

        // "nocategory" omits Category; "explicitnull" sends it as JSON null. Both are null.
        Assert.Equal(["explicitnull", "nocategory"], await FilterIdsAsync(searchClient, "Category eq null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_NeNull_IsTheComplementOfEqNull()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-ne-null");

        Assert.Equal(["alpha", "bravo", "delta"], await FilterIdsAsync(searchClient, "Category ne null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_EqNull_OnNumericField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-null-numeric");

        Assert.Equal(["explicitnull", "nocategory"], await FilterIdsAsync(searchClient, "Rating eq null"));
        Assert.Equal(["alpha", "bravo", "delta"], await FilterIdsAsync(searchClient, "Rating ne null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_EqNull_OnComplexSubField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-null-complex");

        // "explicitnull" has no Address at all; "delta" has one whose City is null.
        Assert.Equal(["delta", "explicitnull"], await FilterIdsAsync(searchClient, "Address/City eq null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_EqNull_OnCollectionField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-null-collection");

        // An absent collection and an empty one both count as null, matching Azure Search.
        Assert.Equal(["delta", "explicitnull"], await FilterIdsAsync(searchClient, "Tags eq null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_NullComparison_CombinesWithOtherClauses()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-null-combined");

        Assert.Equal(
            ["alpha"],
            await FilterIdsAsync(searchClient, "Category eq 'Electronics' and Rating ne null and Rating gt 4"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    // ===== 2. String range comparisons =====

    [Fact]
    public async Task Filter_StringRange_ReturnsLexicographicRange()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-range");

        // Categories: Accessories, Electronics (x2). Ordinal ordering.
        Assert.Equal(["alpha", "delta"], await FilterIdsAsync(searchClient, "Category ge 'E'"));
        Assert.Equal(["bravo"], await FilterIdsAsync(searchClient, "Category lt 'E'"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_StringRange_BoundsAreInclusiveOrExclusiveAsWritten()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-bounds");

        Assert.Equal(["alpha", "delta"], await FilterIdsAsync(searchClient, "Category ge 'Electronics'"));
        Assert.Empty(await FilterIdsAsync(searchClient, "Category gt 'Electronics'"));
        Assert.Equal(
            ["alpha", "bravo", "delta"],
            await FilterIdsAsync(searchClient, "Category le 'Electronics'"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_StringRange_OnSearchableField_IgnoresAnalyzedTerms()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-searchable");

        // Names are Alpha, Bravo, Charlie, Echo and delta. Name is searchable, so each is also
        // indexed lowercased; 'b' separates the two readings, since only the lowercase
        // "delta" is a real value at or above it, while the analyzed tokens "bravo", "charlie"
        // and "echo" all are. Matching anything but delta means the range read analyzed terms.
        Assert.Equal(["delta"], await FilterIdsAsync(searchClient, "Name ge 'b'"));

        Assert.Equal(
            ["delta", "explicitnull", "nocategory"],
            await FilterIdsAsync(searchClient, "Name ge 'Charlie'"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_StringRange_IsCaseSensitive()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-case");

        // Ordinal ordering puts every uppercase letter before every lowercase one, so only
        // the lowercase name sorts above 'a'.
        Assert.Equal(["delta"], await FilterIdsAsync(searchClient, "Name gt 'a'"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_StringRange_ExcludesDocumentsWithNoValue()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-null");

        var ids = await FilterIdsAsync(searchClient, "Category ge 'A'");

        Assert.DoesNotContain("nocategory", ids);
        Assert.DoesNotContain("explicitnull", ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Filter_StringRange_OnComplexSubField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-filter-string-complex");

        // Cities: Seattle (alpha), Tacoma (bravo), Seattle (nocategory).
        Assert.Equal(
            ["alpha", "bravo", "nocategory"],
            await FilterIdsAsync(searchClient, "Address/City ge 'S'"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    // ===== 3. search.ismatch vs search.ismatchscoring =====

    [Fact]
    public async Task SearchIsMatch_DoesNotAffectScoring()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-ismatch-scoring");

        // Every hit scores the same, so search.ismatch cannot reorder results by relevance.
        var scores = await ScoresAsync(searchClient, "search.ismatch('Alpha OR Bravo OR Charlie')");

        Assert.NotEmpty(scores);
        Assert.Single(scores.Distinct());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchIsMatch_SelectsTheSameDocumentsAsScoring()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-ismatch-same-docs");

        // Only the scoring differs; the two functions must select the same documents.
        Assert.Equal(
            await FilterIdsAsync(searchClient, "search.ismatchscoring('Alpha OR Bravo')"),
            await FilterIdsAsync(searchClient, "search.ismatch('Alpha OR Bravo')"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchIsMatch_StillFiltersCorrectly()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-ismatch-filters");

        Assert.Equal(["alpha"], await FilterIdsAsync(searchClient, "search.ismatch('Alpha')"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
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
                new SearchableField("Name") { IsFilterable = true, IsSortable = true },
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("Rating", SearchFieldDataType.Int32) { IsFilterable = true },
                new SearchableField("Tags", collection: true) { IsFilterable = true },
                new ComplexField("Address")
                {
                    Fields =
                    {
                        new SimpleField("City", SearchFieldDataType.String) { IsFilterable = true },
                    }
                },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
        await UploadAsync(searchClient);

        return (indexClient, searchClient, indexName);
    }

    /// <summary>
    /// Uploads the test documents as untyped dictionaries, so that a field can be genuinely
    /// absent from the payload rather than serialized as an explicit null.
    /// </summary>
    private static async Task UploadAsync(SearchClient searchClient)
    {
        var documents = new List<SearchDocument>
        {
            new()
            {
                ["Id"] = "alpha",
                ["Name"] = "Alpha",
                ["Category"] = "Electronics",
                ["Rating"] = 5,
                ["Tags"] = new[] { "red", "blue" },
                ["Address"] = new SearchDocument { ["City"] = "Seattle" },
            },
            new()
            {
                ["Id"] = "bravo",
                ["Name"] = "Bravo",
                ["Category"] = "Accessories",
                ["Rating"] = 3,
                ["Tags"] = new[] { "green" },
                ["Address"] = new SearchDocument { ["City"] = "Tacoma" },
            },
            // Category and Rating simply absent from the payload.
            new()
            {
                ["Id"] = "nocategory",
                ["Name"] = "Charlie",
                ["Tags"] = new[] { "red" },
                ["Address"] = new SearchDocument { ["City"] = "Seattle" },
            },
            // Every nullable field sent as an explicit JSON null, which must behave the same
            // way as omitting it.
            new()
            {
                ["Id"] = "explicitnull",
                ["Name"] = "Echo",
                ["Category"] = null,
                ["Rating"] = null,
                ["Tags"] = null,
                ["Address"] = null,
            },
            // An empty collection and an all-null complex object.
            new()
            {
                ["Id"] = "delta",
                ["Name"] = "delta",
                ["Category"] = "Electronics",
                ["Rating"] = 4,
                ["Tags"] = Array.Empty<string>(),
                ["Address"] = new SearchDocument { ["City"] = null },
            },
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Ids matching a filter, sorted so assertions do not depend on result ordering.
    /// </summary>
    private static async Task<List<string>> FilterIdsAsync(SearchClient searchClient, string filter)
    {
        var results = await SearchAsync(searchClient, filter);

        return results
            .Select(r => (string)r.Document["Id"])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<List<double?>> ScoresAsync(SearchClient searchClient, string filter)
    {
        var results = await SearchAsync(searchClient, filter);
        return results.Select(r => r.Score).ToList();
    }

    private static async Task<List<SearchResult<SearchDocument>>> SearchAsync(
        SearchClient searchClient,
        string filter)
    {
        var options = new SearchOptions { Filter = filter, Size = 50 };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        return await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);
    }
}
