using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers merging a field set to an explicit JSON null, which Azure documents as the way to
/// remove that field's value (issue #71).
/// </summary>
/// <remarks>
/// Driven through the real <see cref="LuceneNetSearchIndexer"/> rather than against
/// <c>MergeDocument</c> directly, because the bug lived in the gap between the two: the null
/// was filtered out while the batch item was turned into Lucene fields, so a unit test of the
/// merge alone would have been given nothing to clear and passed against the broken code.
/// </remarks>
public class MergeNullClearsFieldTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public MergeNullClearsFieldTests()
    {
        _index = new SearchIndex
        {
            Name = "clearable",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true, Filterable = true },
                new SearchField { Name = "Rating", Type = "Edm.Int32", Filterable = true, Facetable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
            ]
        };

        var factory = new SharedDirectoryFactory(_directory);
        _writerFactory = new LuceneNetIndexWriterFactory(factory);
        _indexer = new LuceneNetSearchIndexer(_writerFactory, factory);
        _searcher = new LuceneNetIndexSearcher(factory);
    }

    public void Dispose()
    {
        // Writer before directory: it holds an open handle and commits on dispose.
        _writerFactory.Dispose();
        _directory.Dispose();
    }

    private static JsonObject Doc(string id, string name, int rating, params string[] tags) => new()
    {
        ["Id"] = id,
        ["Name"] = name,
        ["Rating"] = rating,
        ["Tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
    };

    private async Task<JsonObject?> Upload(JsonObject doc, params JsonObject[] merges)
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(doc)]);

        foreach (var merge in merges)
        {
            _indexer.IndexDocuments(_index, [new MergeIndexDocumentAction(merge)]);
        }

        return await _searcher.GetDoc(_index, doc["Id"]!.GetValue<string>());
    }

    /// <summary>
    /// The case from the issue: the merge reported success while leaving the value in place.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_ClearsTheField()
    {
        var doc = await Upload(
            Doc("1", "keep", 5, "a"),
            new JsonObject { ["Id"] = "1", ["Name"] = null });

        Assert.NotNull(doc);
        Assert.Null(doc!["Name"]);
        // Untouched fields survive, which is what separates a merge from an upload.
        Assert.Equal(5, doc["Rating"]?.GetValue<int>());
    }

    /// <summary>
    /// Omitting a field and nulling it mean different things; only the latter clears.
    /// </summary>
    [Fact]
    public async Task Merge_OmittingAField_LeavesItAlone()
    {
        var doc = await Upload(
            Doc("1", "keep", 5, "a"),
            new JsonObject { ["Id"] = "1", ["Rating"] = 6 });

        Assert.Equal("keep", doc?["Name"]?.GetValue<string>());
        Assert.Equal(6, doc?["Rating"]?.GetValue<int>());
    }

    /// <summary>
    /// A cleared string must also lose the unanalyzed <c>__azs_raw__</c> copy that filtering
    /// reads, or the document keeps matching a filter for a value it no longer has — the
    /// failure that motivated clearing every name a field writes rather than just its own.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_AlsoClearsTheFilterableCopy()
    {
        await Upload(
            Doc("1", "keep", 5, "a"),
            new JsonObject { ["Id"] = "1", ["Name"] = null });

        var response = await _searcher.Search(_index, new SearchRequest { Filter = "Name eq 'keep'", Count = true });

        Assert.Equal(0, response.Count);
    }

    /// <summary>
    /// Facetable fields carry doc values alongside the indexed value, so a cleared field must
    /// drop out of its facet buckets too.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_RemovesTheValueFromFacets()
    {
        await Upload(
            Doc("1", "keep", 5, "a"),
            new JsonObject { ["Id"] = "1", ["Rating"] = null });

        var response = await _searcher.Search(_index, new SearchRequest { Facets = ["Rating"], Count = true });

        var buckets = response.Facets?["Rating"];
        Assert.True(buckets is null || buckets.All(b => b.Value?.ToString() != "5"));
    }

    /// <summary>
    /// Collections keep their elements in a JSON sidecar as well as as indexed terms, so both
    /// have to go.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_ClearsACollectionAndItsSidecar()
    {
        var doc = await Upload(
            Doc("1", "keep", 5, "a", "b"),
            new JsonObject { ["Id"] = "1", ["Tags"] = null });

        Assert.Null(doc?["Tags"]);

        var response = await _searcher.Search(_index, new SearchRequest { Filter = "Tags/any(t: t eq 'a')", Count = true });
        Assert.Equal(0, response.Count);
    }

    /// <summary>
    /// Clearing is not the same as deleting: the document and its other fields remain.
    /// </summary>
    [Fact]
    public async Task Merge_ClearingEveryNonKeyField_KeepsTheDocument()
    {
        var doc = await Upload(
            Doc("1", "keep", 5, "a"),
            new JsonObject { ["Id"] = "1", ["Name"] = null, ["Rating"] = null, ["Tags"] = null });

        Assert.NotNull(doc);
        Assert.Equal("1", doc!["Id"]?.GetValue<string>());
    }

    /// <summary>
    /// Nulling a field that has no value is a no-op rather than an error.
    /// </summary>
    [Fact]
    public async Task Merge_WithExplicitNull_OnAnAbsentValue_Succeeds()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(new JsonObject { ["Id"] = "1" })]);

        var result = _indexer.IndexDocuments(_index,
            [new MergeIndexDocumentAction(new JsonObject { ["Id"] = "1", ["Name"] = null })]);

        Assert.True(result.Value.Single().Status);
        Assert.Null((await _searcher.GetDoc(_index, "1"))?["Name"]);
    }

    /// <summary>
    /// Upload replaces the whole document, so a null there means "no value" and must not be
    /// mistaken for the clear instruction it is on a merge.
    /// </summary>
    [Fact]
    public async Task Upload_WithExplicitNull_StillReplacesTheDocument()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "first", 5, "a"))]);
        _indexer.IndexDocuments(_index,
            [new UploadIndexDocumentAction(new JsonObject { ["Id"] = "1", ["Name"] = null, ["Rating"] = 6 })]);

        var doc = await _searcher.GetDoc(_index, "1");

        Assert.Null(doc?["Name"]);
        Assert.Equal(6, doc?["Rating"]?.GetValue<int>());
        // Dropped because upload replaces, not because the null cleared it.
        Assert.Null(doc?["Tags"]);
    }

    /// <summary>
    /// MergeOrUpload takes the same clearing path when the document already exists.
    /// </summary>
    [Fact]
    public async Task MergeOrUpload_WithExplicitNull_ClearsTheField()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "keep", 5, "a"))]);
        _indexer.IndexDocuments(_index,
            [new MergeOrUploadIndexDocumentAction(new JsonObject { ["Id"] = "1", ["Name"] = null })]);

        var doc = await _searcher.GetDoc(_index, "1");

        Assert.Null(doc?["Name"]);
        Assert.Equal(5, doc?["Rating"]?.GetValue<int>());
    }

    /// <summary>
    /// Merging against a document that does not exist still fails with a 404, so adding the
    /// clear step has not turned a missing-document merge into a silent create.
    /// </summary>
    [Fact]
    public void Merge_WithExplicitNull_OnAMissingDocument_Returns404()
    {
        var result = _indexer.IndexDocuments(_index,
            [new MergeIndexDocumentAction(new JsonObject { ["Id"] = "nope", ["Name"] = null })]);

        var single = result.Value.Single();
        Assert.False(single.Status);
        Assert.Equal(404, single.StatusCode);
    }

    /// <summary>
    /// Single-RAMDirectory backed factory pair shared between indexer and searcher so
    /// writes are visible to subsequent reads within the same test.
    /// </summary>
    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory) : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
