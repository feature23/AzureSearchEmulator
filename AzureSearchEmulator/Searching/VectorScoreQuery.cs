using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Matches a fixed set of documents, each with a score decided in advance (issue #46).
/// </summary>
/// <remarks>
/// What <see cref="VectorSearchQuery"/> rewrites into. The nearest-neighbour scan has already
/// chosen the documents and computed their similarities, so all that remains is to present that
/// decision to Lucene as a query — one that matches exactly those documents and reports exactly
/// those scores, leaving the collector, the sort, and the rest of the search pipeline to work
/// unchanged.
///
/// Separating the two also keeps the scan from running more than once. Lucene may call
/// <see cref="Query.CreateWeight"/> and iterate a weight's scorer per segment, but
/// <see cref="Query.Rewrite"/> happens once per search.
/// </remarks>
public class VectorScoreQuery(IReadOnlyDictionary<int, float> hits) : Query
{
    /// <summary>
    /// The matched documents and their scores, keyed by global document id.
    /// </summary>
    public IReadOnlyDictionary<int, float> Hits { get; } = hits;

    public override Weight CreateWeight(IndexSearcher searcher) => new VectorWeight(this);

    public override string ToString(string field) => $"vectorScore({Hits.Count} hits)";

    public override bool Equals(object? obj)
        => obj is VectorScoreQuery other
           && Boost.Equals(other.Boost)
           && Hits.Count == other.Hits.Count
           && Hits.All(i => other.Hits.TryGetValue(i.Key, out var score) && score.Equals(i.Value));

    public override int GetHashCode()
    {
        var hash = new HashCode();

        hash.Add(Boost);

        // Order-independent, because the hits come out of a heap in no particular order and two
        // equal sets must hash alike.
        foreach (var hit in Hits.OrderBy(i => i.Key))
        {
            hash.Add(hit.Key);
            hash.Add(hit.Value);
        }

        return hash.ToHashCode();
    }

    private sealed class VectorWeight(VectorScoreQuery query) : Weight
    {
        public override Query Query => query;

        /// <remarks>
        /// The scan produced final scores, so there is no query normalization to apply: scaling
        /// them by Lucene's usual factors would change the numbers a caller compares against
        /// the similarity the metric defines.
        /// </remarks>
        public override float GetValueForNormalization() => 1f;

        public override void Normalize(float norm, float topLevelBoost)
        {
            // Deliberately empty; see GetValueForNormalization.
        }

        public override Explanation Explain(AtomicReaderContext context, int doc)
        {
            var globalDoc = context.DocBase + doc;

            return query.Hits.TryGetValue(globalDoc, out var score)
                ? new Explanation(score * query.Boost, "vector similarity")
                : new Explanation(0f, "no vector match");
        }

        public override Scorer GetScorer(AtomicReaderContext context, IBits acceptDocs)
        {
            // Hits are keyed globally; each segment serves the slice that falls inside it.
            var maxDoc = context.AtomicReader.MaxDoc;

            var segmentHits = query.Hits
                .Where(i => i.Key >= context.DocBase && i.Key < context.DocBase + maxDoc)
                .Select(i => (Doc: i.Key - context.DocBase, i.Value))
                .Where(i => acceptDocs?.Get(i.Doc) != false)
                .OrderBy(i => i.Doc)
                .ToList();

            return segmentHits.Count == 0 ? null! : new VectorScorer(this, segmentHits, query.Boost);
        }
    }

    /// <remarks>
    /// Documents are handed out in ascending order, which is what a
    /// <see cref="DocIdSetIterator"/> contract requires and what collectors assume.
    /// </remarks>
    private sealed class VectorScorer(
        Weight weight,
        IReadOnlyList<(int Doc, float Score)> hits,
        float boost)
        : Scorer(weight)
    {
        private int _index = -1;

        public override int DocID => _index < 0
            ? -1
            : _index >= hits.Count
                ? NO_MORE_DOCS
                : hits[_index].Doc;

        public override int Freq => 1;

        /// <remarks>
        /// Bounds-checked because <see cref="Advance"/> ends by calling
        /// <see cref="NextDoc"/>, which can leave the scorer past the last hit — and a collector
        /// that scores defensively after exhaustion would otherwise crash the query rather than
        /// simply see no more documents.
        /// </remarks>
        public override float GetScore()
            => _index < 0 || _index >= hits.Count ? 0f : hits[_index].Score * boost;

        public override int NextDoc()
        {
            _index++;

            return DocID;
        }

        public override int Advance(int target)
        {
            while (_index + 1 < hits.Count && hits[_index + 1].Doc < target)
            {
                _index++;
            }

            return NextDoc();
        }

        public override long GetCost() => hits.Count;
    }
}
