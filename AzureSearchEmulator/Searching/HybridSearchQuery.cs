using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Answers a hybrid query — a full-text query together with one or more vector queries — by
/// fusing each arm's ranking with Reciprocal Rank Fusion (issue #46).
/// </summary>
/// <remarks>
/// <para>
/// The arms cannot simply be unioned. A BM25 score is unbounded and a vector score sits in
/// <c>(0, 1]</c>, so a union would let whichever arm happened to produce larger numbers decide
/// the ranking outright, regardless of how well the other arm rated the same documents. RRF
/// discards the scores and fuses on rank, which every arm expresses on one scale — see
/// <see cref="ReciprocalRankFusion"/>.
/// </para>
/// <para>
/// Fusing on rank is why this is a query in its own right rather than a
/// <see cref="BooleanQuery"/> over the arms: a rank is a property of a whole result list, not
/// of one document, so every arm has to be run and ordered before any document's score is
/// known. Each arm is executed in <see cref="Rewrite"/>, and what comes out is a
/// <see cref="VectorScoreQuery"/> over the fused ranking — which leaves the collector, paging,
/// <c>$select</c> and highlighting pipeline working exactly as it does for any other query.
/// </para>
/// <para>
/// One arm per <em>field</em>, not per vector query. Azure counts each field of a vector query
/// as its own query execution, so a text query alongside one vector query naming two fields
/// fuses three lists rather than two. <see cref="VectorQuerySupport"/> builds the per-field
/// queries; this class only fuses what it is given.
/// </para>
/// </remarks>
public class HybridSearchQuery : Query
{
    private readonly Query? _textQuery;
    private readonly IReadOnlyList<VectorSearchQuery> _vectorQueries;
    private readonly int _textRecallSize;

    /// <param name="textQuery">
    /// The full-text arm, or null for a vector-only fusion of several vector queries.
    /// </param>
    /// <param name="textRecallSize">
    /// How many documents the text arm contributes to the fusion. Azure calls this
    /// <c>maxTextRecallSize</c> and defaults it to 1000 — far above the page size, because the
    /// fusion needs enough of each arm's ranking to find the documents both arms agree on.
    /// </param>
    public HybridSearchQuery(
        Query? textQuery,
        IReadOnlyList<VectorSearchQuery> vectorQueries,
        int textRecallSize = VectorQuerySupport.DefaultTextRecallSize)
    {
        _textQuery = textQuery;
        _vectorQueries = vectorQueries;
        _textRecallSize = textRecallSize;
    }

    public override Query Rewrite(IndexReader reader)
    {
        var arms = new List<ReciprocalRankFusion.Arm>(_vectorQueries.Count + 1);

        if (_textQuery is { } textQuery)
        {
            arms.Add(new ReciprocalRankFusion.Arm(GetTextRanking(textQuery, reader)));
        }

        foreach (var vectorQuery in _vectorQueries)
        {
            arms.Add(new ReciprocalRankFusion.Arm(
                vectorQuery.GetRankedDocIds(reader),
                vectorQuery.Weight));
        }

        var fused = ReciprocalRankFusion.Fuse(arms);

        if (fused.Count == 0)
        {
            return new BooleanQuery();
        }

        return new VectorScoreQuery(fused.ToDictionary(i => i.DocId, i => i.Score))
        {
            Boost = Boost
        };
    }

    /// <summary>
    /// Runs the text arm and returns its documents in relevance order.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="_textRecallSize"/> rather than by the page size, because a document
    /// the text arm ranks 200th may still finish near the top of the fused ranking if a vector
    /// arm rates it highly — which is the case hybrid search exists to serve.
    /// </remarks>
    private IReadOnlyList<int> GetTextRanking(Query textQuery, IndexReader reader)
    {
        var searcher = new IndexSearcher(reader);

        var docs = searcher.Search(textQuery, _textRecallSize);

        return docs.ScoreDocs.Select(i => i.Doc).ToList();
    }

    public override string ToString(string field)
        => $"hybrid({(_textQuery == null ? "" : "text + ")}{_vectorQueries.Count} vector)";

    public override bool Equals(object? obj)
        => obj is HybridSearchQuery other
           && Equals(_textQuery, other._textQuery)
           && _vectorQueries.SequenceEqual(other._vectorQueries)
           && _textRecallSize == other._textRecallSize
           && Boost.Equals(other.Boost);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(_textQuery);

        foreach (var query in _vectorQueries)
        {
            hash.Add(query);
        }

        hash.Add(_textRecallSize);
        hash.Add(Boost);

        return hash.ToHashCode();
    }
}
