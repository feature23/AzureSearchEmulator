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
/// Covers the fields a merge does not supply. They must stay searchable, filterable and
/// facetable, not only retrievable.
/// </summary>
/// <remarks>
/// A merge used to write back the stored fields of the old document as they were. Stored fields
/// carry no indexed terms or doc values, so after any merge the document still came back from a
/// lookup by key but no longer matched a filter or a search on its other fields.
/// </remarks>
public class MergeKeepsFieldsIndexedTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public MergeKeepsFieldsIndexedTests()
    {
        _index = new SearchIndex
        {
            Name = "mergeable",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "Source", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Content", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Groups", Type = "Collection(Edm.String)", Searchable = false, Filterable = true },
                new SearchField { Name = "Rating", Type = "Edm.Int32", Filterable = true, Facetable = true },
                new SearchField { Name = "Published", Type = "Edm.Boolean", Filterable = true },
                new SearchField { Name = "Updated", Type = "Edm.DateTimeOffset", Filterable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true },
                new SearchField
                {
                    Name = "Address",
                    Type = "Edm.ComplexType",
                    Fields = [new SearchField { Name = "City", Type = "Edm.String", Searchable = false, Filterable = true }],
                },
                new SearchField
                {
                    Name = "Files",
                    Type = "Collection(Edm.ComplexType)",
                    Fields = [new SearchField { Name = "Path", Type = "Edm.String", Searchable = false, Filterable = true }],
                },
                new SearchField { Name = "Title", Type = "Edm.String", Searchable = true, Filterable = true },
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

    // Parsed from text, as a request body is: a date built in C# as a string JsonValue cannot be
    // read back as a DateTimeOffset.
    private static JsonObject Doc(string id) => JsonNode.Parse($$"""
        {
          "Id": "{{id}}",
          "Source": "blob-{{id}}",
          "Content": "quarterly board minutes",
          "Groups": ["finance", "legal"],
          "Rating": 4,
          "Published": true,
          "Updated": "2025-03-01T00:00:00Z",
          "Location": { "type": "Point", "coordinates": [-1.55, 53.8] },
          "Address": { "City": "Leeds" },
          "Files": [{ "Path": "Entities/Company 4" }],
          "Title": "Old title"
        }
        """)!.AsObject();

    private static JsonObject TitleChange(string id) => new() { ["Id"] = id, ["Title"] = "New title" };

    private void Index(IndexDocumentAction action)
    {
        var result = Assert.Single(_indexer.IndexDocuments(_index, [action]).Value);
        Assert.True(result.Status, result.ErrorMessage);
    }

    private async Task<int?> Count(SearchRequest request)
    {
        request.Count = true;
        return (await _searcher.Search(_index, request)).Count;
    }

    public static TheoryData<string> FiltersOnUntouchedFields =>
    [
        "Id eq '1' and Source eq 'blob-1'",
        "Groups/any(g: search.in(g, 'finance'))",
        "Rating eq 4",
        "Published eq true",
        "Updated eq 2025-03-01T00:00:00Z",
        "geo.distance(Location, geography'POINT(-1.55 53.8)') le 1",
        "Address/City eq 'Leeds'",
        "Files/any(f: f/Path eq 'Entities/Company 4')",
    ];

    [Theory]
    [MemberData(nameof(FiltersOnUntouchedFields))]
    public async Task Merge_LeavesUntouchedFieldsFilterable(string filter)
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeIndexDocumentAction(TitleChange("1")));

        Assert.Equal(1, await Count(new SearchRequest { Filter = filter }));
    }

    [Theory]
    [MemberData(nameof(FiltersOnUntouchedFields))]
    public async Task MergeOrUpload_OnExistingDocument_LeavesUntouchedFieldsFilterable(string filter)
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeOrUploadIndexDocumentAction(TitleChange("1")));

        Assert.Equal(1, await Count(new SearchRequest { Filter = filter }));
    }

    [Fact]
    public async Task Merge_LeavesUntouchedFieldsSearchable()
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeIndexDocumentAction(TitleChange("1")));

        Assert.Equal(1, await Count(new SearchRequest { Search = "minutes", SearchFields = "Content" }));
    }

    [Fact]
    public async Task Merge_LeavesUntouchedFieldsFacetable()
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeIndexDocumentAction(TitleChange("1")));

        var response = await _searcher.Search(_index, new SearchRequest { Facets = ["Rating"] });

        var bucket = Assert.Single(response.Facets!["Rating"]);
        Assert.Equal(1, bucket.Count);
    }

    [Fact]
    public async Task Merge_ReplacesTheSuppliedFieldInTheIndex()
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeIndexDocumentAction(TitleChange("1")));

        Assert.Equal(0, await Count(new SearchRequest { Filter = "Title eq 'Old title'" }));
        Assert.Equal(1, await Count(new SearchRequest { Filter = "Title eq 'New title'" }));
        Assert.Equal(1, await Count(new SearchRequest { Search = "new", SearchFields = "Title" }));
    }

    /// <summary>
    /// A second merge reads back the document the first one wrote, so it must also find every
    /// field stored.
    /// </summary>
    [Fact]
    public async Task RepeatedMerges_KeepEveryFieldRetrievable()
    {
        Index(new UploadIndexDocumentAction(Doc("1")));
        Index(new MergeIndexDocumentAction(TitleChange("1")));
        Index(new MergeIndexDocumentAction(new JsonObject { ["Id"] = "1", ["Rating"] = 5 }));

        var doc = await _searcher.GetDoc(_index, "1");

        Assert.NotNull(doc);
        Assert.Equal(Doc("1").Select(p => p.Key).Order(), doc!.Select(p => p.Key).Order());
        Assert.Equal("blob-1", doc["Source"]?.GetValue<string>());
        Assert.Equal("New title", doc["Title"]?.GetValue<string>());
        Assert.Equal(5, doc["Rating"]?.GetValue<int>());
        Assert.Equal("Leeds", doc["Address"]?["City"]?.GetValue<string>());
        Assert.Equal(1, await Count(new SearchRequest { Filter = "Rating eq 5 and Source eq 'blob-1'" }));
    }

    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory) : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
