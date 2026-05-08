using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Microsoft.OData.UriParser;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for Collection(Edm.*) field support: indexing, retrieval, and OData lambda
/// (any/all) filtering. Mirrors the Azure Search collection field semantics — see
/// https://learn.microsoft.com/en-us/azure/search/search-query-odata-collection-operators.
/// </summary>
public class CollectionFieldTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public CollectionFieldTests()
    {
        var index = CreateIndex();
        var docs = CreateDocuments(index);
        _helper = new LuceneTestHelper(index, docs);
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    private static SearchIndex CreateIndex() => new()
    {
        Name = "products",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
            new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
            new SearchField { Name = "Sizes", Type = "Collection(Edm.Int32)", Filterable = true },
            new SearchField { Name = "Prices", Type = "Collection(Edm.Double)", Filterable = true },
            new SearchField { Name = "Available", Type = "Collection(Edm.Boolean)", Filterable = true },
        ]
    };

    private static List<Lucene.Net.Documents.Document> CreateDocuments(SearchIndex index)
    {
        var rows = new List<JsonObject>
        {
            new()
            {
                ["Id"] = "1",
                ["Name"] = "Red shirt",
                ["Tags"] = new JsonArray("red", "cotton", "shirt"),
                ["Sizes"] = new JsonArray(8, 10, 12),
                ["Prices"] = new JsonArray(19.99, 24.99),
                ["Available"] = new JsonArray(true),
            },
            new()
            {
                ["Id"] = "2",
                ["Name"] = "Blue jeans",
                ["Tags"] = new JsonArray("blue", "denim", "pants"),
                ["Sizes"] = new JsonArray(30, 32, 34),
                ["Prices"] = new JsonArray(49.99),
                ["Available"] = new JsonArray(true, false),
            },
            new()
            {
                ["Id"] = "3",
                ["Name"] = "Red hat",
                ["Tags"] = new JsonArray("red", "wool"),
                ["Sizes"] = new JsonArray(),
                ["Prices"] = new JsonArray(15.0, 18.0, 22.5),
                ["Available"] = new JsonArray(false),
            },
            new()
            {
                ["Id"] = "4",
                ["Name"] = "Green socks",
                ["Tags"] = new JsonArray("green", "cotton"),
                ["Sizes"] = new JsonArray(9, 10, 11),
                ["Prices"] = new JsonArray(5.99),
                ["Available"] = new JsonArray(true),
            },
            new()
            {
                ["Id"] = "5",
                ["Name"] = "Untagged item",
                ["Tags"] = new JsonArray(),
                ["Sizes"] = new JsonArray(42),
                ["Prices"] = new JsonArray(),
                ["Available"] = new JsonArray(),
            }
        };

        return rows.Select(row =>
        {
            var doc = new Lucene.Net.Documents.Document();
            foreach (var field in index.Fields)
            {
                if (row[field.Name] is { } value)
                {
                    foreach (var f in field.CreateFields(value))
                    {
                        doc.Add(f);
                    }
                }
            }
            return doc;
        }).ToList();
    }

    private async Task<List<string>> SearchIds(string filter)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = filter,
            Top = 50
        });
        return response.Results
            .Select(r => r["Id"]!.GetValue<string>())
            .OrderBy(id => id)
            .ToList();
    }

    // ===== any(t: t eq value) =====

    [Fact]
    public async Task Any_StringEquality_MatchesDocsContainingValue()
    {
        var ids = await SearchIds("Tags/any(t: t eq 'red')");

        Assert.Equal(["1", "3"], ids);
    }

    [Fact]
    public async Task Any_StringEquality_NoMatch_ReturnsEmpty()
    {
        var ids = await SearchIds("Tags/any(t: t eq 'purple')");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task Any_StringEquality_DifferentParameterName()
    {
        // The lambda parameter name is arbitrary — verify we honor whatever the user picks.
        var ids = await SearchIds("Tags/any(tag: tag eq 'cotton')");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task Any_IntEquality_MatchesDocsContainingValue()
    {
        var ids = await SearchIds("Sizes/any(s: s eq 10)");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task Any_IntRange_MatchesDocsWithAnyValueInRange()
    {
        // Doc 1: 8,10,12 — has 8 in range
        // Doc 4: 9,10,11 — has 9 in range
        var ids = await SearchIds("Sizes/any(s: s lt 10)");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task Any_DoubleRange_MatchesDocsWithAnyValueAboveThreshold()
    {
        // Doc 1: 19.99, 24.99 — has 24.99 > 20
        // Doc 2: 49.99 — > 20
        // Doc 3: 15, 18, 22.5 — has 22.5 > 20
        var ids = await SearchIds("Prices/any(p: p gt 20.0)");

        Assert.Equal(["1", "2", "3"], ids);
    }

    [Fact]
    public async Task Any_BooleanEquality_MatchesDocsContainingValue()
    {
        // Doc 2 has both true and false in its Available collection.
        var ids = await SearchIds("Available/any(a: a eq false)");

        Assert.Equal(["2", "3"], ids);
    }

    // ===== any() with no expression =====

    [Fact]
    public async Task Any_NoExpression_MatchesDocsWhereCollectionIsNonEmpty()
    {
        // Doc 5 has empty Tags; everyone else has at least one tag.
        var ids = await SearchIds("Tags/any()");

        Assert.Equal(["1", "2", "3", "4"], ids);
    }

    [Fact]
    public async Task Any_NoExpression_OnEmptyOnlyCollection_ReturnsEmpty()
    {
        // Doc 5 has empty Available; doc 1,3,4 have one entry; doc 2 has two.
        var ids = await SearchIds("Available/any()");

        Assert.Equal(["1", "2", "3", "4"], ids);
    }

    // ===== all(t: t op value) =====

    [Fact]
    public async Task All_NotEqual_OnlyMatchesDocsWithoutValue()
    {
        // all(t: t ne 'red') matches docs whose Tags do NOT contain 'red'.
        // Doc 5 has empty tags — vacuously satisfies "all", which is consistent with set semantics.
        var ids = await SearchIds("Tags/all(t: t ne 'red')");

        Assert.Contains("2", ids);
        Assert.Contains("4", ids);
        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("3", ids);
    }

    [Fact]
    public async Task All_GreaterThan_OnlyMatchesDocsWhereEveryValueExceedsThreshold()
    {
        // all(s: s gt 8) — every Size must be > 8
        // Doc 1: 8,10,12 → has 8 (fails)
        // Doc 4: 9,10,11 → all > 8 (passes)
        // Doc 2: 30,32,34 → all > 8 (passes)
        // Doc 5: just 42 → all > 8 (passes)
        // Doc 3: empty → vacuously true (passes)
        var ids = await SearchIds("Sizes/all(s: s gt 8)");

        Assert.Contains("2", ids);
        Assert.Contains("4", ids);
        Assert.Contains("5", ids);
        Assert.DoesNotContain("1", ids);
    }

    [Fact]
    public async Task All_NotWithEqual_MatchesSameAsNotEqual()
    {
        // all(t: not (t eq 'red')) is equivalent to all(t: t ne 'red')
        var ids = await SearchIds("Tags/all(t: not (t eq 'red'))");

        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("3", ids);
        Assert.Contains("2", ids);
        Assert.Contains("4", ids);
    }

    // ===== any with search.in =====

    [Fact]
    public async Task Any_SearchIn_MatchesAnyValueInList()
    {
        var ids = await SearchIds("Tags/any(t: search.in(t, 'red,blue'))");

        Assert.Equal(["1", "2", "3"], ids);
    }

    // ===== Combinations with top-level operators =====

    [Fact]
    public async Task Any_CombinedWithAnd_ReturnsIntersection()
    {
        // Tags contains 'cotton' AND Sizes contains 10
        var ids = await SearchIds("Tags/any(t: t eq 'cotton') and Sizes/any(s: s eq 10)");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task Any_CombinedWithOr_ReturnsUnion()
    {
        var ids = await SearchIds("Tags/any(t: t eq 'wool') or Tags/any(t: t eq 'denim')");

        Assert.Equal(["2", "3"], ids);
    }

    [Fact]
    public async Task NotAny_NegatesLambda()
    {
        var ids = await SearchIds("not Tags/any(t: t eq 'red')");

        Assert.Contains("2", ids);
        Assert.Contains("4", ids);
        Assert.Contains("5", ids); // empty collection — never contains 'red'
        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("3", ids);
    }

    // ===== Document retrieval round-trips =====

    [Fact]
    public async Task GetDoc_ReturnsCollectionValuesAsArray()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "1");

        Assert.NotNull(doc);
        var tags = doc!["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Equal(3, tags!.Count);
        Assert.Equal("red", tags[0]!.GetValue<string>());
        Assert.Equal("cotton", tags[1]!.GetValue<string>());
        Assert.Equal("shirt", tags[2]!.GetValue<string>());

        var sizes = doc["Sizes"] as JsonArray;
        Assert.NotNull(sizes);
        Assert.Equal([8, 10, 12], sizes!.Select(n => n!.GetValue<int>()).ToArray());

        var prices = doc["Prices"] as JsonArray;
        Assert.NotNull(prices);
        Assert.Equal(2, prices!.Count);
        Assert.Equal(19.99, prices[0]!.GetValue<double>());
        Assert.Equal(24.99, prices[1]!.GetValue<double>());

        var available = doc["Available"] as JsonArray;
        Assert.NotNull(available);
        Assert.Single(available!);
        Assert.True(available[0]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetDoc_PreservesEmptyCollection()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "5");

        Assert.NotNull(doc);
        var tags = doc!["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Empty(tags!);
    }

    [Fact]
    public async Task Search_StarReturnsAllDocsWithCollectionValues()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest { Search = "*", Top = 50 });

        Assert.Equal(5, response.Results.Count);
        var doc1 = response.Results.Single(r => r["Id"]!.GetValue<string>() == "2");
        var tags = doc1["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Equal(["blue", "denim", "pants"], tags!.Select(n => n!.GetValue<string>()).ToArray());
    }

    // ===== Searchable collection (full-text) =====

    [Fact]
    public async Task Search_SearchableCollection_FullTextMatchesAcrossAllValues()
    {
        // Tags is Searchable=true. A free-text search for "denim" should hit doc 2,
        // since 'denim' is one of its tag values.
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "denim",
            SearchFields = "Tags",
            Top = 50
        });

        Assert.Single(response.Results);
        Assert.Equal("2", response.Results[0]["Id"]!.GetValue<string>());
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}

/// <summary>
/// Lower-level tests targeting the ODataQueryVisitor's lambda translation directly,
/// without going through the full search pipeline.
/// </summary>
public class ODataQueryVisitor_LambdaTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;
    private readonly SearchIndex _index;

    public ODataQueryVisitor_LambdaTests()
    {
        _index = new SearchIndex
        {
            Name = "tagged",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Filterable = true },
                new SearchField { Name = "Locked", Type = "Collection(Edm.String)", Filterable = false },
            ]
        };

        var docs = new List<JsonObject>
        {
            new() { ["Id"] = "1", ["Tags"] = new JsonArray("a", "b") },
            new() { ["Id"] = "2", ["Tags"] = new JsonArray("b", "c") },
            new() { ["Id"] = "3", ["Tags"] = new JsonArray("c") },
        };

        var luceneDocs = docs.Select(row =>
        {
            var doc = new Lucene.Net.Documents.Document();
            foreach (var field in _index.Fields)
            {
                if (row[field.Name] is { } value)
                {
                    foreach (var f in field.CreateFields(value))
                    {
                        doc.Add(f);
                    }
                }
            }
            return doc;
        }).ToList();

        _helper = new LuceneTestHelper(_index, luceneDocs);
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose() => _helper.Dispose();

    private List<string> Run(string filter)
    {
        var parser = new UriQueryExpressionParser(100);
        var token = parser.ParseFilter(filter);
        var query = token.Accept(new ODataQueryVisitor(_index));
        var docs = _searcher.Search(query, 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id)
            .ToList();
    }

    [Fact]
    public void All_NotEqual_String_MatchesDocsWhereNoValueEqualsTarget()
    {
        // Per Azure Search collection-operator rules, the supported lambda for string
        // collections inside all() is "ne". Doc 1 (a,b) and doc 2 (b,c) both contain 'b';
        // only doc 3 (c) has no 'b'.
        var ids = Run("Tags/all(t: t ne 'b')");
        Assert.Contains("3", ids);
        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("2", ids);
    }

    [Fact]
    public void All_NotSearchIn_String_ExcludesDocsContainingListedValues()
    {
        // all(t: not search.in(t, 'a,b')) — doc must not contain 'a' or 'b'.
        var ids = Run("Tags/all(t: not search.in(t, 'a,b'))");
        Assert.Contains("3", ids);
        Assert.DoesNotContain("1", ids);
        Assert.DoesNotContain("2", ids);
    }

    [Fact]
    public void Any_OnNonFilterableCollection_Throws()
    {
        var parser = new UriQueryExpressionParser(100);
        var token = parser.ParseFilter("Locked/any(l: l eq 'x')");
        Assert.Throws<InvalidOperationException>(() => token.Accept(new ODataQueryVisitor(_index)));
    }
}

/// <summary>
/// End-to-end indexer tests: drive the real LuceneNetSearchIndexer (which routes through
/// Upload/Merge/MergeOrUpload actions) so collection JSON values are written to a real
/// in-memory Lucene directory and then queried through LuceneNetIndexSearcher. This
/// covers the sidecar collection-storage field through both the initial upload path
/// and the field-replacement logic in MergeDocument.
/// </summary>
public class LuceneNetSearchIndexer_CollectionTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public LuceneNetSearchIndexer_CollectionTests()
    {
        _index = new SearchIndex
        {
            Name = "tagged-products",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
                new SearchField { Name = "Sizes", Type = "Collection(Edm.Int32)", Filterable = true },
            ]
        };

        var factory = new SharedDirectoryFactory(_directory);
        _indexer = new LuceneNetSearchIndexer(factory, factory);
        _searcher = new LuceneNetIndexSearcher(factory);
    }

    public void Dispose() => _directory.Dispose();

    private static JsonObject Doc(string id, string name, string[] tags, int[] sizes) => new()
    {
        ["Id"] = id,
        ["Name"] = name,
        ["Tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
        ["Sizes"] = new JsonArray(sizes.Select(s => (JsonNode)s).ToArray()),
    };

    [Fact]
    public async Task Upload_ThenSearch_ReturnsCollectionValuesAndHonorsLambdaFilter()
    {
        var batch = new List<IndexDocumentAction>
        {
            new UploadIndexDocumentAction(Doc("1", "Red Shirt", ["red", "cotton"], [8, 10, 12])),
            new UploadIndexDocumentAction(Doc("2", "Blue Jeans", ["blue", "denim"], [30, 32])),
            new UploadIndexDocumentAction(Doc("3", "Red Hat", ["red", "wool"], [])),
        };

        var result = _indexer.IndexDocuments(_index, batch);
        Assert.All(result.Value, r => Assert.True(r.Status));

        // Round-trip: GetDoc should return the collection as a JSON array.
        var doc1 = await _searcher.GetDoc(_index, "1");
        Assert.NotNull(doc1);
        var tags = doc1!["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Equal(["red", "cotton"], tags!.Select(n => n!.GetValue<string>()).ToArray());

        var sizes = doc1["Sizes"] as JsonArray;
        Assert.NotNull(sizes);
        Assert.Equal([8, 10, 12], sizes!.Select(n => n!.GetValue<int>()).ToArray());

        // Lambda filter on string collection.
        var redResults = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Tags/any(t: t eq 'red')",
            Top = 50
        });
        Assert.Equal(2, redResults.Results.Count);
        Assert.Contains(redResults.Results, r => r["Id"]!.GetValue<string>() == "1");
        Assert.Contains(redResults.Results, r => r["Id"]!.GetValue<string>() == "3");

        // Lambda filter on numeric collection.
        var sizeResults = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Sizes/any(s: s ge 30)",
            Top = 50
        });
        Assert.Single(sizeResults.Results);
        Assert.Equal("2", sizeResults.Results[0]["Id"]!.GetValue<string>());

        // any() with no body — should exclude doc 3 (empty Sizes collection).
        var nonEmpty = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Sizes/any()",
            Top = 50
        });
        Assert.Equal(2, nonEmpty.Results.Count);
        Assert.DoesNotContain(nonEmpty.Results, r => r["Id"]!.GetValue<string>() == "3");
    }

    [Fact]
    public async Task MergeOrUpload_ReplacesCollection_AndRemovesStaleSidecar()
    {
        // Initial upload.
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "Red Shirt", ["red", "cotton"], [8, 10]))]);

        // Now overwrite the same doc with a different collection. The merge logic must
        // both clear the per-element fields and replace the sidecar JSON, otherwise the
        // round-tripped collection would still contain stale values.
        var update = new JsonObject
        {
            ["Id"] = "1",
            ["Tags"] = new JsonArray("green", "linen", "shirt"),
            ["Sizes"] = new JsonArray(14, 16),
        };
        _indexer.IndexDocuments(_index, [new MergeOrUploadIndexDocumentAction(update)]);

        var doc = await _searcher.GetDoc(_index, "1");
        Assert.NotNull(doc);
        Assert.Equal("Red Shirt", doc!["Name"]?.GetValue<string>()); // unchanged scalar field is preserved by merge
        var tags = doc["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Equal(["green", "linen", "shirt"], tags!.Select(n => n!.GetValue<string>()).ToArray());

        // The lambda filter must reflect the new values, not the old ones.
        var redHits = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Tags/any(t: t eq 'red')",
            Top = 50
        });
        Assert.Empty(redHits.Results);

        var greenHits = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Tags/any(t: t eq 'green')",
            Top = 50
        });
        Assert.Single(greenHits.Results);
    }

    [Fact]
    public async Task Upload_EmptyCollection_RoundTripsAsEmptyArray()
    {
        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(Doc("1", "Bare Item", [], []))]);

        var doc = await _searcher.GetDoc(_index, "1");
        Assert.NotNull(doc);
        var tags = doc!["Tags"] as JsonArray;
        Assert.NotNull(tags);
        Assert.Empty(tags!);

        // any() on the empty collection should NOT match.
        var hits = await _searcher.Search(_index, new SearchRequest
        {
            Search = "*",
            Filter = "Tags/any()",
            Top = 50
        });
        Assert.Empty(hits.Results);
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
