using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for issue #71 — merging a field set to an explicit JSON null, which Azure
/// documents as the way to remove that field's value — run against a containerized emulator
/// through the Azure Search SDK.
/// </summary>
/// <remarks>
/// The unit tests drive the indexer directly; these exist because the bug is only reachable
/// through the shape of the request, and the SDK is how callers actually produce that shape.
/// Documents are sent as untyped <see cref="SearchDocument"/> dictionaries throughout, because
/// a POCO cannot distinguish a property left at its default from one deliberately set to null —
/// which is the very distinction under test.
/// </remarks>
public class MergeNullClearsFieldIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>
    /// The case from the issue: the merge reported success while leaving the value in place.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_ClearsTheField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-clears");

        await MergeAsync(searchClient, new SearchDocument { ["Id"] = "alpha", ["Category"] = null });

        var doc = await GetAsync(searchClient, "alpha");

        AssertCleared(doc, "Category");
        // Untouched fields survive, which is what separates a merge from an upload.
        Assert.Equal("Alpha", doc["Name"]);
        Assert.Equal(5, doc["Rating"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Omitting a field and nulling it mean different things; only the latter clears.
    /// </summary>
    [Fact]
    public async Task Merge_OmittingAField_LeavesItAlone()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-omitted-kept");

        await MergeAsync(searchClient, new SearchDocument { ["Id"] = "alpha", ["Rating"] = 6 });

        var doc = await GetAsync(searchClient, "alpha");

        Assert.Equal("Electronics", doc["Category"]);
        Assert.Equal(6, doc["Rating"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A cleared field has to leave the filterable copy behind as well as the retrievable one,
    /// or the document keeps matching a filter for a value it no longer has. This is the
    /// assertion retrieval alone cannot make.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_RemovesTheDocumentFromFilterResults()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-filter");

        Assert.Contains("alpha", await FilterIdsAsync(searchClient, "Category eq 'Electronics'"));

        await MergeAsync(searchClient, new SearchDocument { ["Id"] = "alpha", ["Category"] = null });

        Assert.DoesNotContain("alpha", await FilterIdsAsync(searchClient, "Category eq 'Electronics'"));
        // And it is now findable as a null, the complement of the same filter.
        Assert.Contains("alpha", await FilterIdsAsync(searchClient, "Category eq null"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Facetable fields carry doc values alongside the indexed value, so a cleared field must
    /// drop out of its facet buckets too.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_RemovesTheValueFromFacets()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-facet");

        await MergeAsync(searchClient, new SearchDocument { ["Id"] = "alpha", ["Category"] = null });

        var options = new SearchOptions { Facets = { "Category" }, Size = 0 };
        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var electronics = response.Value.Facets?["Category"]
            .FirstOrDefault(f => (string?)f.Value == "Electronics");

        // "alpha" was the only Electronics document, so the bucket goes with it.
        Assert.True(electronics is null || electronics.Count == 0);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Collections keep their elements in a JSON sidecar as well as as indexed terms, so both
    /// have to go.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_ClearsACollection()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-collection");

        await MergeAsync(searchClient, new SearchDocument { ["Id"] = "alpha", ["Tags"] = null });

        var doc = await GetAsync(searchClient, "alpha");
        // An emptied collection may come back absent or as an empty array; both are cleared.
        Assert.True(
            !doc.ContainsKey("Tags") || doc["Tags"] is not IEnumerable<object> tags || !tags.Any(),
            "Expected 'Tags' to have been cleared, but the document still carries elements.");

        Assert.DoesNotContain("alpha", await FilterIdsAsync(searchClient, "Tags/any(t: t eq 'red')"));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Clearing is not the same as deleting: the document and its key remain.
    /// </summary>
    [Fact]
    public async Task Merge_ClearingEveryNonKeyField_KeepsTheDocument()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-all-fields");

        await MergeAsync(searchClient, new SearchDocument
        {
            ["Id"] = "alpha",
            ["Name"] = null,
            ["Category"] = null,
            ["Rating"] = null,
            ["Tags"] = null,
        });

        var doc = await GetAsync(searchClient, "alpha");

        Assert.Equal("alpha", doc["Id"]);
        AssertCleared(doc, "Category");
        AssertCleared(doc, "Name");
        AssertCleared(doc, "Rating");

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Nulling a field that has no value is a no-op rather than an error.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_OnAnAbsentValue_Succeeds()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-absent");

        // "nocategory" never had a Category to begin with.
        var result = await MergeAsync(searchClient, new SearchDocument { ["Id"] = "nocategory", ["Category"] = null });

        Assert.True(result.Results.Single().Succeeded);
        AssertCleared(await GetAsync(searchClient, "nocategory"), "Category");

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// MergeOrUpload takes the same clearing path when the document already exists.
    /// </summary>
    [Fact]
    public async Task MergeOrUpload_WithExplicitNull_ClearsTheField()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-mergeorupload-null");

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.MergeOrUpload([new SearchDocument { ["Id"] = "alpha", ["Category"] = null }]),
            cancellationToken: TestContext.Current.CancellationToken);

        var doc = await GetAsync(searchClient, "alpha");

        AssertCleared(doc, "Category");
        Assert.Equal(5, doc["Rating"]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Merging against a document that does not exist still fails with a per-item 404, so
    /// adding the clear step has not turned a missing-document merge into a silent create.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_OnAMissingDocument_Fails()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-merge-null-missing");

        var result = await MergeAsync(searchClient, new SearchDocument { ["Id"] = "nope", ["Category"] = null });

        var single = result.Results.Single();
        Assert.False(single.Succeeded);
        Assert.Equal(404, single.Status);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Upload replaces the whole document, so a null there means "no value" and must not be
    /// mistaken for the clear instruction it is on a merge.
    /// </summary>
    [Fact]
    public async Task Upload_WithExplicitNull_StillReplacesTheDocument()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-upload-null-replaces");

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload([new SearchDocument
            {
                ["Id"] = "alpha",
                ["Name"] = null,
                ["Rating"] = 6,
            }]),
            cancellationToken: TestContext.Current.CancellationToken);

        var doc = await GetAsync(searchClient, "alpha");

        AssertCleared(doc, "Name");
        Assert.Equal(6, doc["Rating"]);
        // Dropped because upload replaces the document, not because the null cleared it.
        AssertCleared(doc, "Category");

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
                new SearchableField("Name") { IsFilterable = true },
                // Filterable and facetable so a clear can be observed through both, not just
                // through retrieval.
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("Rating", SearchFieldDataType.Int32) { IsFilterable = true },
                new SearchableField("Tags", collection: true) { IsFilterable = true },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var documents = new List<SearchDocument>
        {
            new()
            {
                ["Id"] = "alpha",
                ["Name"] = "Alpha",
                ["Category"] = "Electronics",
                ["Rating"] = 5,
                ["Tags"] = new[] { "red", "blue" },
            },
            new()
            {
                ["Id"] = "bravo",
                ["Name"] = "Bravo",
                ["Category"] = "Accessories",
                ["Rating"] = 3,
                ["Tags"] = new[] { "green" },
            },
            // Category absent from the payload entirely, for the clear-an-absent-value case.
            new()
            {
                ["Id"] = "nocategory",
                ["Name"] = "Charlie",
                ["Rating"] = 4,
            },
        };

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        return (indexClient, searchClient, indexName);
    }

    private static async Task<IndexDocumentsResult> MergeAsync(SearchClient searchClient, SearchDocument document)
    {
        var response = await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Merge([document]),
            cancellationToken: TestContext.Current.CancellationToken);

        return response.Value;
    }

    /// <summary>
    /// Asserts a field carries no value for this document.
    /// </summary>
    /// <remarks>
    /// A field with no value is omitted from the response body rather than returned as a JSON
    /// null, which is what Azure Search does too — so <see cref="SearchDocument"/>'s indexer
    /// throws <see cref="KeyNotFoundException"/> rather than handing back null. Absent and
    /// explicitly null therefore both count as cleared.
    /// </remarks>
    private static void AssertCleared(SearchDocument document, string fieldName)
    {
        Assert.True(
            !document.ContainsKey(fieldName) || document[fieldName] is null,
            $"Expected '{fieldName}' to have been cleared, but the document still carries a value for it.");
    }

    private static async Task<SearchDocument> GetAsync(SearchClient searchClient, string key)
    {
        var response = await searchClient.GetDocumentAsync<SearchDocument>(
            key, cancellationToken: TestContext.Current.CancellationToken);

        return response.Value;
    }

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
