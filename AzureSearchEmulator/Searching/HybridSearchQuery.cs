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
    private readonly Filter? _preFilter;
    private readonly int _textRecallSize;

    /// <param name="textQuery">
    /// The full-text arm, or null for a vector-only fusion of several vector queries.
    /// </param>
    /// <param name="preFilter">
    /// Restricts the text arm to documents passing the request's <c>$filter</c>, matching what
    /// the vector arms already do under <c>preFilter</c> mode. Null leaves the filter to the
    /// surrounding search, which is <c>postFilter</c>.
    /// </param>
    public HybridSearchQuery(
        Query? textQuery,
        IReadOnlyList<VectorSearchQuery> vectorQueries,
        Filter? preFilter = null)
    {
        _textQuery = textQuery;
        _vectorQueries = vectorQueries;
        _preFilter = preFilter;
        _textRecallSize = VectorQuerySupport.DefaultTextRecallSize;
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
            // Boost multiplies the weight rather than being dropped. A boost and a weight are
            // the same lever from two directions — one set on the Lucene query, one on the
            // request — and the single-arm path already applies boost, so ignoring it here would
            // make a query score differently depending on how many arms it was fused with.
            arms.Add(new ReciprocalRankFusion.Arm(
                vectorQuery.GetRankedDocIds(reader),
                vectorQuery.Weight * vectorQuery.Boost));
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
    /// The full-text arm, or null when the fusion is over vector queries alone.
    /// </summary>
    /// <remarks>
    /// Exposed for hit highlighting, which has to be built from the text query rather than the
    /// fused one. Overriding <see cref="Query.ExtractTerms"/> here does not work: Lucene's
    /// <c>QueryScorer</c> rewrites before extracting, and this query rewrites into a set of
    /// document ids that has no terms at all — so the highlighter saw none, scored every fragment
    /// zero, and returned an empty <c>@search.highlights</c> with no error to say why.
    /// </remarks>
    public Query? TextQuery => _textQuery;

    /// <summary>
    /// Runs the text arm and returns its documents in relevance order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded by <see cref="_textRecallSize"/> rather than by the page size, because a document
    /// the text arm ranks 200th may still finish near the top of the fused ranking if a vector
    /// arm rates it highly — which is the case hybrid search exists to serve.
    /// </para>
    /// <para>
    /// The filter is applied here rather than only around the fused result, for the same reason
    /// it is applied to the vector arms: a bounded window over an unfiltered ranking can be
    /// filled entirely by documents the filter excludes, leaving the ones that pass it with no
    /// text contribution to the fusion at all. Ranking within the filtered set is what
    /// <c>preFilter</c> means, and applying it to one arm but not the other would make a
    /// document's fused score depend on which arm happened to see the filter.
    /// </para>
    /// </remarks>
    private IReadOnlyList<int> GetTextRanking(Query textQuery, IndexReader reader)
    {
        var searcher = new IndexSearcher(reader);

        var docs = searcher.Search(textQuery, _preFilter, _textRecallSize);

        return docs.ScoreDocs.Select(i => i.Doc).ToList();
    }

    public override string ToString(string field)
        => $"hybrid({(_textQuery == null ? "" : "text + ")}{_vectorQueries.Count} vector)";

    public override bool Equals(object? obj)
        => obj is HybridSearchQuery other
           && Equals(_textQuery, other._textQuery)
           && _vectorQueries.SequenceEqual(other._vectorQueries)
           && Equals(_preFilter, other._preFilter)
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

        // A Filter has no dependable GetHashCode, so only its presence is folded in — enough to
        // keep equal objects hashing equally, which is the direction that matters.
        hash.Add(_preFilter != null);

        return hash.ToHashCode();
    }
}
