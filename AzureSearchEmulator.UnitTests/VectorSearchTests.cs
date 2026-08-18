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
/// End-to-end tests for answering a vector query, driving the real indexer and searcher
/// (issue #46).
/// </summary>
/// <remarks>
/// The vectors are deliberately simple — unit vectors along the axes and a few points between
/// them — so the expected ranking is obvious by inspection rather than an artefact of whatever
/// the arithmetic happened to produce.
/// </remarks>
public class VectorSearchTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public VectorSearchTests()
    {
        _index = CreateIndex(VectorSearchMetric.Cosine);

        var factory = new SharedDirectoryFactory(_directory);
        _writerFactory = new LuceneNetIndexWriterFactory(factory);
        _indexer = new LuceneNetSearchIndexer(_writerFactory, factory);
        _searcher = new LuceneNetIndexSearcher(factory);
    }

    public void Dispose()
    {
        _writerFactory.Dispose();
        _directory.Dispose();
    }

    private static SearchIndex CreateIndex(VectorSearchMetric metric) => new()
    {
        Name = "vectors",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
            new SearchField { Name = "Category", Type = "Edm.String", Searchable = true, Filterable = true },
            new SearchField
            {
                Name = "Embedding",
                Type = "Collection(Edm.Single)",
                Searchable = true,
                Filterable = false,
                Dimensions = 3,
                VectorSearchProfile = "vp"
            },
        ],
        VectorSearch = new VectorSearch
        {
            Algorithms =
            [
                new VectorSearchAlgorithm
                {
                    Name = "algo",
                    Kind = VectorSearchAlgorithmKind.Hnsw,
                    HnswParameters = new HnswParameters { Metric = metric }
                }
            ],
            Profiles = [new VectorSearchProfile { Name = "vp", Algorithm = "algo" }]
        }
    };

    private static JsonObject Doc(string id, string category, float[] embedding) => new()
    {
        ["Id"] = id,
        ["Category"] = category,
        ["Embedding"] = new JsonArray(embedding.Select(f => (JsonNode)f).ToArray()),
    };

    /// <summary>
    /// Three unit vectors along the axes, plus one between x and y. A query along x should rank
    /// x first, then the diagonal, then the two orthogonal axes.
    /// </summary>
    private void IndexAxisDocuments()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("x", "axis", [1f, 0f, 0f])),
            new UploadIndexDocumentAction(Doc("y", "axis", [0f, 1f, 0f])),
            new UploadIndexDocumentAction(Doc("z", "axis", [0f, 0f, 1f])),
            new UploadIndexDocumentAction(Doc("xy", "diagonal", [0.7071f, 0.7071f, 0f])),
        ]);

        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));
    }

    private static SearchRequest VectorRequest(float[] vector, int? k = null, string? fields = "Embedding")
        => new()
        {
            VectorQueries = [new VectorQuery
            {
                Kind = "vector",
                Vector = vector,
                Fields = fields,
                KNearestNeighborsCount = k
            }]
        };

    private static string[] Ids(SearchResponse response)
        => response.Results.Select(i => i["Id"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task VectorQuery_RanksByProximity()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f]));

        // x is identical to the query, xy is 45° away, y and z are orthogonal.
        Assert.Equal("x", Ids(response)[0]);
        Assert.Equal("xy", Ids(response)[1]);
    }

    [Fact]
    public async Task VectorQuery_ScoresAreDescending()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f]));

        var scores = response.Results.Select(i => i["@search.score"]!.GetValue<float>()).ToArray();

        Assert.Equal(scores.OrderByDescending(i => i), scores);
    }

    /// <summary>
    /// The one metric whose absolute score Azure documents: an exact match scores 1, and an
    /// orthogonal vector scores 1 / (1 + 1).
    /// </summary>
    [Fact]
    public async Task VectorQuery_UsesAzuresDocumentedCosineScore()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f]));

        var byId = response.Results.ToDictionary(
            i => i["Id"]!.GetValue<string>(),
            i => i["@search.score"]!.GetValue<float>());

        Assert.Equal(1.0, byId["x"], 5);
        Assert.Equal(0.5, byId["y"], 5);
    }

    [Fact]
    public async Task VectorQuery_HonoursK()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 2));

        Assert.Equal(2, response.Results.Count);
        Assert.Equal(["x", "xy"], Ids(response));
    }

    /// <summary>
    /// k larger than the corpus is not an error; it simply cannot be filled.
    /// </summary>
    [Fact]
    public async Task VectorQuery_WithKAboveCorpusSize_ReturnsEverything()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 1000));

        Assert.Equal(4, response.Results.Count);
    }

    /// <summary>
    /// k and $top are different knobs: k chooses how many neighbours the vector query
    /// contributes, $top how many of them a page returns.
    /// </summary>
    [Fact]
    public async Task Top_PagesTheNeighboursKSelected()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 3);
        request.Top = 2;

        var response = await _searcher.Search(_index, request);

        Assert.Equal(2, response.Results.Count);
        Assert.Equal(["x", "xy"], Ids(response));
    }

    [Fact]
    public async Task Skip_PagesThroughTheNeighbours()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 4);
        request.Skip = 1;
        request.Top = 1;

        var response = await _searcher.Search(_index, request);

        Assert.Equal(["xy"], Ids(response));
    }

    [Fact]
    public async Task Count_ReportsTheNumberOfNeighbours()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 3);
        request.Count = true;

        var response = await _searcher.Search(_index, request);

        Assert.Equal(3, response.Count);
    }

    /// <summary>
    /// preFilter is the default, and it applies before the neighbours are chosen — so the
    /// result is the nearest documents <em>among those that pass the filter</em>, and a full k
    /// of them where enough exist.
    /// </summary>
    [Fact]
    public async Task PreFilter_NarrowsTheCandidatesBeforeSelection()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 2);
        request.Filter = "Category eq 'axis'";

        var response = await _searcher.Search(_index, request);

        // xy is nearer than y and z but is excluded, so the two nearest 'axis' documents come
        // back rather than one.
        Assert.Equal(2, response.Results.Count);
        Assert.Equal("x", Ids(response)[0]);
        Assert.DoesNotContain("xy", Ids(response));
    }

    /// <summary>
    /// postFilter applies after selection, so an excluded document still consumes one of the k
    /// slots and the result can be shorter than k. This is the difference the mode exists to
    /// express, and the reason preFilter is the default.
    /// </summary>
    [Fact]
    public async Task PostFilter_AppliesAfterSelection_AndCanReturnFewerThanK()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 2);
        request.Filter = "Category eq 'axis'";
        request.VectorFilterMode = "postFilter";

        var response = await _searcher.Search(_index, request);

        // The two nearest are x and xy; xy is then filtered out, leaving one.
        Assert.Equal(["x"], Ids(response));
    }

    /// <summary>
    /// A document with no vector has no similarity to the query at all, so it is left out
    /// rather than ranked last — absence is not distance.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutAVector_IsNotReturned()
    {
        IndexAxisDocuments();

        _indexer.IndexDocuments(_index, [new UploadIndexDocumentAction(new JsonObject
        {
            ["Id"] = "novector",
            ["Category"] = "axis"
        })]);

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 100));

        Assert.DoesNotContain("novector", Ids(response));
        Assert.Equal(4, response.Results.Count);
    }

    [Fact]
    public async Task DeletedDocument_IsNotReturned()
    {
        IndexAxisDocuments();

        _indexer.IndexDocuments(_index, [new DeleteIndexDocumentAction(new JsonObject { ["Id"] = "x" })]);

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 100));

        Assert.DoesNotContain("x", Ids(response));
        Assert.Equal(3, response.Results.Count);
    }

    /// <summary>
    /// A vector field is usually hidden, and hiding it must not stop it being searched — only
    /// stop it coming back.
    /// </summary>
    [Fact]
    public async Task HiddenVectorField_IsStillSearchable()
    {
        _index.Fields[2].Retrievable = false;
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 1));

        Assert.Equal(["x"], Ids(response));
        Assert.False(response.Results[0].ContainsKey("Embedding"));
    }

    [Fact]
    public async Task Select_AppliesToVectorResults()
    {
        IndexAxisDocuments();

        var request = VectorRequest([1f, 0f, 0f], k: 1);
        request.Select = "Id";

        var response = await _searcher.Search(_index, request);

        Assert.Equal(["Id", "@search.score"], response.Results[0].Select(i => i.Key));
    }

    /// <summary>
    /// Naming no fields searches every vector field in the index, which is what Azure does.
    /// </summary>
    [Fact]
    public async Task VectorQuery_WithoutFields_SearchesEveryVectorField()
    {
        IndexAxisDocuments();

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f], k: 1, fields: null));

        Assert.Equal(["x"], Ids(response));
    }

    /// <summary>
    /// An index whose documents were all deleted still has to answer a vector query, with an
    /// empty result rather than an error.
    /// </summary>
    [Fact]
    public async Task IndexWithNoMatchingDocuments_ReturnsNoResults()
    {
        IndexAxisDocuments();

        _indexer.IndexDocuments(_index,
        [
            new DeleteIndexDocumentAction(new JsonObject { ["Id"] = "x" }),
            new DeleteIndexDocumentAction(new JsonObject { ["Id"] = "y" }),
            new DeleteIndexDocumentAction(new JsonObject { ["Id"] = "z" }),
            new DeleteIndexDocumentAction(new JsonObject { ["Id"] = "xy" }),
        ]);

        var response = await _searcher.Search(_index, VectorRequest([1f, 0f, 0f]));

        Assert.Empty(response.Results);
    }

    [Theory]
    [InlineData(VectorSearchMetric.Cosine)]
    [InlineData(VectorSearchMetric.DotProduct)]
    [InlineData(VectorSearchMetric.Euclidean)]
    public async Task EveryMetric_RanksTheNearestFirst(VectorSearchMetric metric)
    {
        var index = CreateIndex(metric);
        IndexAxisDocuments();

        var response = await _searcher.Search(index, VectorRequest([1f, 0f, 0f], k: 1));

        Assert.Equal(["x"], Ids(response));
    }

    /// <summary>
    /// The metric comes from the field's profile, and the three order these documents
    /// differently — which is the observable proof that the profile is actually consulted
    /// rather than cosine being assumed.
    /// </summary>
    [Fact]
    public async Task Metric_ChangesTheRanking()
    {
        // A short vector pointing exactly at the query, and a long one pointing near it.
        // Cosine prefers the aligned short vector; dot product prefers the long one.
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("aligned", "a", [0.1f, 0f, 0f])),
            new UploadIndexDocumentAction(Doc("long", "b", [5f, 2f, 0f])),
        ]);
        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));

        var cosine = await _searcher.Search(
            CreateIndex(VectorSearchMetric.Cosine), VectorRequest([1f, 0f, 0f], k: 1));
        var dotProduct = await _searcher.Search(
            CreateIndex(VectorSearchMetric.DotProduct), VectorRequest([1f, 0f, 0f], k: 1));

        Assert.Equal(["aligned"], Ids(cosine));
        Assert.Equal(["long"], Ids(dotProduct));
    }

    /// <summary>
    /// Single-RAMDirectory backed factory pair shared between indexer and searcher so writes are
    /// visible to subsequent reads within the same test.
    /// </summary>
    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory)
        : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
