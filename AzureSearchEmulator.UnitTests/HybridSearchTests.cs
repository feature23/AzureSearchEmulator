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
/// End-to-end tests for hybrid search — a text query fused with vector queries by Reciprocal
/// Rank Fusion (issue #46).
/// </summary>
/// <remarks>
/// The documents are arranged so the two arms disagree, because agreement proves nothing: any
/// combination strategy ranks a document both arms love at the top. What distinguishes RRF from
/// a union is what it does when one arm is enthusiastic and the other is not, so that is what
/// these tests set up.
/// </remarks>
public class HybridSearchTests : IDisposable
{
    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public HybridSearchTests()
    {
        _index = CreateIndex();

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

    private static SearchIndex CreateIndex() => new()
    {
        Name = "hybrid",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
            new SearchField { Name = "Title", Type = "Edm.String", Searchable = true, Filterable = true },
            new SearchField
            {
                Name = "Embedding",
                Type = "Collection(Edm.Single)",
                Searchable = true,
                Filterable = false,
                Dimensions = 3,
                VectorSearchProfile = "vp"
            },
            new SearchField
            {
                Name = "Alternate",
                Type = "Collection(Edm.Single)",
                Searchable = true,
                Filterable = false,
                Dimensions = 3,
                VectorSearchProfile = "vp"
            },
        ],
        VectorSearch = new VectorSearch
        {
            Algorithms = [new VectorSearchAlgorithm { Name = "algo" }],
            Profiles = [new VectorSearchProfile { Name = "vp", Algorithm = "algo" }]
        }
    };

    private static JsonObject Doc(string id, string title, float[] embedding, float[]? alternate = null)
    {
        var doc = new JsonObject
        {
            ["Id"] = id,
            ["Title"] = title,
            ["Embedding"] = new JsonArray(embedding.Select(f => (JsonNode)f).ToArray()),
        };

        if (alternate != null)
        {
            doc["Alternate"] = new JsonArray(alternate.Select(f => (JsonNode)f).ToArray());
        }

        return doc;
    }

    /// <summary>
    /// Four documents arranged so the arms genuinely disagree:
    /// <list type="bullet">
    /// <item>textonly — matches the text query, and points away from the query vector.</item>
    /// <item>vectoronly — identical to the query vector, and matches no text at all.</item>
    /// <item>both — matches the text, and sits second-nearest to the query vector.</item>
    /// <item>filler — matches the text, and points away from the query vector, so that
    /// <c>textonly</c> does not have the text arm's lower ranks to itself.</item>
    /// </list>
    /// Neither arm ranks <c>both</c> first: the vector arm prefers <c>vectoronly</c>, and the
    /// text arm ranks the three text matches equally, so the tie-break decides among them. Only
    /// a strategy that rewards a document for placing well in <em>both</em> puts <c>both</c> on
    /// top.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text scores deliberately tie. Lucene normalizes term frequency, so repeating a term
    /// does not reliably outrank a single occurrence, and building the fixture on the assumption
    /// that it does produced a test that passed for the wrong reason. Equal text scores make the
    /// vector arm the only thing that can separate these documents, which is exactly what the
    /// fusion is being asked to demonstrate.
    /// </para>
    /// <para>
    /// The vector arm is deliberately narrow (<c>k</c> of 2 by default here). A vector query
    /// always returns its full <c>k</c> if the index holds that many documents, however poor the
    /// match — so a wide <c>k</c> would put every document in the vector arm too, and a document
    /// that merely happens to be least-bad would collect a second term it has not earned.
    /// </para>
    /// </remarks>
    private void IndexDisagreeingDocuments()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("textonly", "widget", [0f, 0f, 1f])),
            new UploadIndexDocumentAction(Doc("vectoronly", "unrelated", [1f, 0f, 0f])),
            new UploadIndexDocumentAction(Doc("both", "widget", [0.9f, 0.1f, 0f])),
            new UploadIndexDocumentAction(Doc("filler", "widget", [0f, -1f, 0f])),
        ]);

        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));
    }

    private static SearchRequest HybridRequest(
        string? search,
        float[] vector,
        int k = 2,
        string fields = "Embedding",
        float? weight = null)
        => new()
        {
            Search = search,
            VectorQueries = [new VectorQuery
            {
                Kind = "vector",
                Vector = vector,
                Fields = fields,
                KNearestNeighborsCount = k,
                Weight = weight
            }]
        };

    private static string[] Ids(SearchResponse response)
        => response.Results.Select(i => i["Id"]!.GetValue<string>()).ToArray();

    /// <summary>
    /// The property hybrid search exists for: a document both arms rate reasonably beats one
    /// that a single arm rates best. A union of raw scores could not produce this, since the
    /// arms' scores are on unrelated scales.
    /// </summary>
    [Fact]
    public async Task DocumentRatedByBothArms_OutranksSingleArmFavourites()
    {
        IndexDisagreeingDocuments();

        var response = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f]));

        Assert.Equal("both", Ids(response)[0]);
    }

    /// <summary>
    /// A document only one arm returns still appears — it contributes a single term rather than
    /// being excluded for its absence from the other.
    /// </summary>
    [Fact]
    public async Task DocumentInOneArmOnly_StillAppears()
    {
        IndexDisagreeingDocuments();

        var response = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f]));

        Assert.Contains("textonly", Ids(response));
        Assert.Contains("vectoronly", Ids(response));
    }

    /// <summary>
    /// The score becomes the RRF score, which is small by construction — one arm contributes at
    /// most 1/61. Azure warns that such a score still indicates a strong match.
    /// </summary>
    [Fact]
    public async Task HybridScore_IsTheRrfScore()
    {
        IndexDisagreeingDocuments();

        var response = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f]));

        var scores = response.Results.ToDictionary(
            i => i["Id"]!.GetValue<string>(),
            i => i["@search.score"]!.GetValue<float>());

        // "both" places second in each arm: the text arm ranks the three matches equally and
        // the tie-break puts it second, and the vector arm ranks it behind "vectoronly".
        Assert.Equal(2f / 62f, scores["both"], 6);

        // "vectoronly" is first in the vector arm and absent from the text arm, so it carries a
        // single term.
        Assert.Equal(1f / 61f, scores["vectoronly"], 6);
    }

    /// <summary>
    /// The value Azure's own documentation reports for a document ranked first by both arms,
    /// reproduced end to end rather than only in the fusion's unit tests.
    /// </summary>
    [Fact]
    public async Task DocumentRankedFirstByBothArms_ScoresAzuresPublishedValue()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("winner", "widget", [1f, 0f, 0f])),
        ]);
        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));

        var response = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f]));

        Assert.Equal(0.032786883413791656f, response.Results[0]["@search.score"]!.GetValue<float>());
    }

    /// <summary>
    /// Weighting an arm scales every term it contributes, so a document the vector arm returns
    /// gains on one it does not.
    /// </summary>
    /// <remarks>
    /// Asserted as a change in relative score rather than a reordering, because no weight can
    /// reorder <em>these</em> documents: "both" places second in both arms, so lifting the
    /// vector arm lifts it too — <c>w/62 + 1/62</c> stays above <c>w/61</c> for every positive
    /// <c>w</c>. A test demanding a flip here would be demanding something RRF does not do.
    /// </remarks>
    [Fact]
    public async Task Weight_ScalesTheVectorArmsContribution()
    {
        IndexDisagreeingDocuments();

        var unweighted = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f]));
        var weighted = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f], weight: 10f));

        static float ScoreOf(SearchResponse response, string id)
            => response.Results.Single(i => i["Id"]!.GetValue<string>() == id)["@search.score"]!.GetValue<float>();

        // Returned only by the vector arm, so its whole score scales with the weight.
        Assert.Equal(1f / 61f, ScoreOf(unweighted, "vectoronly"), 6);
        Assert.Equal(10f / 61f, ScoreOf(weighted, "vectoronly"), 6);

        // Returned only by the text arm, so the weight leaves it untouched.
        Assert.Equal(ScoreOf(unweighted, "filler"), ScoreOf(weighted, "filler"), 6);
    }

    /// <summary>
    /// A weight can reorder the fused ranking when the arms genuinely disagree about which
    /// document comes first, which is the point of exposing it.
    /// </summary>
    [Fact]
    public async Task Weight_CanReorderTheFusedRanking()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            // First in the text arm, and far from the query vector.
            new UploadIndexDocumentAction(Doc("texty", "widget", [0f, 0f, 1f])),
            // First in the vector arm, and matches no text.
            new UploadIndexDocumentAction(Doc("vectory", "unrelated", [1f, 0f, 0f])),
        ]);
        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));

        // Unweighted both score 1/61, and the tie-break decides.
        var unweighted = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f], k: 1));
        Assert.Equal("texty", Ids(unweighted)[0]);

        // Weighting the vector arm up breaks the tie the other way.
        var weighted = await _searcher.Search(_index, HybridRequest("widget", [1f, 0f, 0f], k: 1, weight: 2f));
        Assert.Equal("vectory", Ids(weighted)[0]);
    }

    /// <summary>
    /// Each field of a vector query is its own arm, so a query naming two fields fuses two
    /// vector rankings alongside the text one. A document rated by both fields therefore
    /// accumulates two vector terms.
    /// </summary>
    [Fact]
    public async Task EachFieldIsItsOwnArm()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            // Nearest on both vector fields, but no text match at all.
            new UploadIndexDocumentAction(Doc("twofields", "unrelated", [1f, 0f, 0f], [1f, 0f, 0f])),
            // Nearest on one field only, and matches the text.
            new UploadIndexDocumentAction(Doc("onefield", "widget", [1f, 0f, 0f], [0f, 0f, 1f])),
        ]);
        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));

        var response = await _searcher.Search(
            _index, HybridRequest("widget", [1f, 0f, 0f], fields: "Embedding,Alternate"));

        var scores = response.Results.ToDictionary(
            i => i["Id"]!.GetValue<string>(),
            i => i["@search.score"]!.GetValue<float>());

        // twofields is first in both vector arms: 2/61. onefield is first in one vector arm and
        // first in the text arm, also 2/61 — but second in the other vector arm, so it wins.
        Assert.True(scores["onefield"] > scores["twofields"]);
    }

    /// <summary>
    /// Several vector queries with no text query are fused too: two vector rankings are no more
    /// comparable to each other than a vector ranking is to a text one.
    /// </summary>
    [Fact]
    public async Task SeveralVectorQueries_AreFusedWithoutATextArm()
    {
        var result = _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(Doc("a", "one", [1f, 0f, 0f], [0f, 1f, 0f])),
            new UploadIndexDocumentAction(Doc("b", "two", [0f, 1f, 0f], [1f, 0f, 0f])),
        ]);
        Assert.All(result.Value, r => Assert.True(r.Status, r.ErrorMessage));

        var response = await _searcher.Search(_index, new SearchRequest
        {
            VectorQueries =
            [
                new VectorQuery { Kind = "vector", Vector = [1f, 0f, 0f], Fields = "Embedding" },
                new VectorQuery { Kind = "vector", Vector = [1f, 0f, 0f], Fields = "Alternate" },
            ]
        });

        // Each is first in one arm and second in the other, so both score 1/61 + 1/62 and the
        // tie-break decides — the point being that the scores are fused, not raw similarities.
        var scores = response.Results.Select(i => i["@search.score"]!.GetValue<float>()).ToArray();

        Assert.All(scores, score => Assert.Equal(1f / 61f + 1f / 62f, score, 6));
    }

    /// <summary>
    /// A single vector query and no text query is not a fusion, so it keeps the raw similarity
    /// score rather than a rank-derived one. This is the phase 2 behaviour, which must survive.
    /// </summary>
    [Fact]
    public async Task SingleVectorQuery_KeepsItsSimilarityScore()
    {
        IndexDisagreeingDocuments();

        var response = await _searcher.Search(_index, new SearchRequest
        {
            VectorQueries =
            [
                new VectorQuery { Kind = "vector", Vector = [1f, 0f, 0f], Fields = "Embedding" }
            ]
        });

        // An exact match scores 1 under the documented cosine transform, not 1/61.
        Assert.Equal(1f, response.Results[0]["@search.score"]!.GetValue<float>(), 5);
    }

    [Fact]
    public async Task Filter_AppliesToBothArms()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Filter = "Id eq 'both'";

        var response = await _searcher.Search(_index, request);

        Assert.Equal(["both"], Ids(response));
    }

    [Fact]
    public async Task Top_PagesTheFusedRanking()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Top = 2;

        var response = await _searcher.Search(_index, request);

        Assert.Equal(2, response.Results.Count);
        Assert.Equal("both", Ids(response)[0]);
    }

    [Fact]
    public async Task Count_ReportsTheFusedTotal()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Count = true;

        var response = await _searcher.Search(_index, request);

        Assert.Equal(4, response.Count);
    }

    /// <summary>
    /// A text query matching nothing leaves the vector arm to supply the whole ranking, rather
    /// than emptying the result.
    /// </summary>
    [Fact]
    public async Task TextArmMatchingNothing_LeavesTheVectorArmToAnswer()
    {
        IndexDisagreeingDocuments();

        var response = await _searcher.Search(_index, HybridRequest("nothingmatchesthis", [1f, 0f, 0f]));

        Assert.Equal("vectoronly", Ids(response)[0]);
    }

    /// <summary>
    /// Hit highlighting still works on a hybrid query.
    /// </summary>
    /// <remarks>
    /// The highlighter scores fragments by the terms the query it was built from reports, and a
    /// fused query rewrites into a set of document ids, which has no terms at all. Left to the
    /// default that produced an empty <c>@search.highlights</c> on every hybrid result, with no
    /// error to say why — the text arm's terms have to be reported explicitly.
    /// </remarks>
    [Fact]
    public async Task Highlighting_WorksUnderHybrid()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Highlight = "Title";

        var response = await _searcher.Search(_index, request);

        var highlighted = response.Results
            .Select(i => i["@search.highlights"]?["Title"]?[0]?.GetValue<string>())
            .FirstOrDefault(i => i != null);

        Assert.NotNull(highlighted);
        Assert.Contains("<em>widget</em>", highlighted);
    }

    /// <summary>
    /// The text arm ranks within the filter, not around it.
    /// </summary>
    /// <remarks>
    /// The arm takes a bounded window of its ranking — 1000 documents by default — and an
    /// unfiltered window can be filled entirely by documents the filter excludes, leaving the
    /// ones that pass with no text contribution to the fusion. Ranking inside the filtered set is
    /// what <c>preFilter</c> means, and the vector arms already did it; applying it to one arm
    /// and not the other would make a fused score depend on which arm saw the filter.
    /// </remarks>
    [Fact]
    public async Task PreFilter_AppliesToTheTextArmToo()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Filter = "Id eq 'both' or Id eq 'filler'";

        var response = await _searcher.Search(_index, request);

        // Only the two documents the filter admits, and each carries a first-or-second text term
        // rather than whatever rank it held in the unfiltered ranking.
        Assert.Equal(2, response.Results.Count);
        Assert.All(Ids(response), id => Assert.Contains(id, new[] { "both", "filler" }));

        var top = response.Results[0]["@search.score"]!.GetValue<float>();

        // Once the filter narrows both arms to these two documents, "both" leads each of them —
        // the text arm on the tie-break and the vector arm on proximity — so it carries two
        // first-place terms. Without the filter reaching the text arm it would have ranked
        // behind the documents the filter excludes.
        Assert.Equal("both", Ids(response)[0]);
        Assert.Equal(2f / 61f, top, 6);
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

    /// <summary>
    /// A hybrid result is ranked by fused rank, not by the relevance score
    /// <c>search.score()</c> names, so Azure rejects the combination outright rather than
    /// sorting by a score that does not apply (issue #48).
    /// </summary>
    [Fact]
    public async Task OrderBySearchScore_OnAHybridQuery_Throws()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest("widget", [1f, 0f, 0f]);
        request.Orderby = "search.score() desc";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _searcher.Search(_index, request));

        Assert.Contains("search.score()", ex.Message);
    }

    /// <summary>
    /// The same restriction applies to a pure vector query, which is scored by similarity.
    /// </summary>
    [Fact]
    public async Task OrderBySearchScore_OnAPureVectorQuery_Throws()
    {
        IndexDisagreeingDocuments();

        var request = HybridRequest(null, [1f, 0f, 0f]);
        request.Orderby = "search.score() desc";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _searcher.Search(_index, request));

        Assert.Contains("search.score()", ex.Message);
    }

}
