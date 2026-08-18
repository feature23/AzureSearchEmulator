using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Matches the <c>k</c> documents nearest a query vector, scored by their similarity
/// (issue #46).
/// </summary>
/// <remarks>
/// <para>
/// The search is exhaustive: every document holding a vector for the field is scored, and the
/// best <c>k</c> are kept. Azure reaches the same answer approximately, through an HNSW graph
/// it builds at index time, and accepts a small loss of recall for the speed that buys at
/// service scale. The emulator has no such pressure — a test index is thousands of documents,
/// not millions, where a full scan is a few milliseconds — and the exchange runs the other way:
/// an exact answer is deterministic, so a test asserting which documents came back cannot
/// become flaky because a graph traversal took a different path.
/// </para>
/// <para>
/// This is why <c>hnsw</c> and <c>exhaustiveKnn</c> behave identically here, and why the graph
/// tuning parameters are accepted and ignored. The one part of the algorithm configuration that
/// does change an answer — the metric — is honoured.
/// </para>
/// <para>
/// Selection happens in <see cref="Rewrite"/> rather than in a <see cref="Filter"/>, because
/// top-<c>k</c> is not a per-document predicate: whether a document belongs in the result
/// depends on every other document's score, which a filter evaluating one document at a time
/// cannot know. The scan therefore runs once, up front, and rewrites into a query over the
/// exact set of documents it chose.
/// </para>
/// </remarks>
public class VectorSearchQuery : Query
{
    private readonly IReadOnlyList<string> _paths;
    private readonly float[] _vector;
    private readonly VectorSearchMetric _metric;
    private readonly int _k;
    private readonly Filter? _preFilter;

    /// <param name="paths">
    /// The vector fields to search. A document is scored on its best field, matching how Azure
    /// treats a query naming several.
    /// </param>
    /// <param name="preFilter">
    /// Restricts which documents the scan considers, implementing <c>vectorFilterMode:
    /// preFilter</c>. Null scans everything.
    /// </param>
    public VectorSearchQuery(
        IReadOnlyList<string> paths,
        float[] vector,
        VectorSearchMetric metric,
        int k,
        Filter? preFilter = null)
    {
        _paths = paths;
        _vector = vector;
        _metric = metric;
        _k = k;
        _preFilter = preFilter;
    }

    public override Query Rewrite(IndexReader reader)
    {
        var hits = FindNearest(reader);

        if (hits.Count == 0)
        {
            // A query matching nothing still has to be a query; BooleanQuery with no clause
            // matches no documents, which is the answer.
            return new BooleanQuery();
        }

        var query = new VectorScoreQuery(hits) { Boost = Boost };

        return query;
    }

    /// <summary>
    /// Scans every candidate document and returns the best <paramref name="_k"/>, keyed by
    /// global document id.
    /// </summary>
    /// <remarks>
    /// Walks one segment at a time because doc values are read per segment, and translates each
    /// segment-local id into the global one the rewritten query is scored against.
    /// </remarks>
    private Dictionary<int, float> FindNearest(IndexReader reader)
    {
        // Min-heap on score: the cheapest way to keep the best k is to hold k and evict the
        // worst whenever something better arrives.
        var best = new PriorityQueue<int, float>(_k);

        var buffer = new float[_vector.Length];
        var bytes = new BytesRef();

        foreach (var context in reader.Leaves)
        {
            var atomicReader = context.AtomicReader;
            var liveDocs = atomicReader.LiveDocs;

            // preFilter narrows the candidate set before any similarity is computed, which is
            // both the semantics Azure documents and much the cheaper order: skipping a
            // document costs a bit test, scoring one costs a full pass over its vector.
            var accepted = _preFilter?.GetDocIdSet(context, liveDocs);
            var acceptedBits = accepted?.Bits;
            var acceptedIterator = acceptedBits == null ? accepted?.GetIterator() : null;

            // A filter that matched nothing in this segment can be skipped whole.
            if (accepted != null && acceptedBits == null && acceptedIterator == null)
            {
                continue;
            }

            var readers = _paths
                .Select(path => atomicReader.GetBinaryDocValues(
                    VectorSearchSupport.GetVectorDocValuesFieldName(path)))
                .Where(i => i != null)
                .ToList();

            // None of the named fields exists in this segment, so no document in it can match.
            if (readers.Count == 0)
            {
                continue;
            }

            var nextAccepted = acceptedIterator?.NextDoc() ?? -1;

            for (var doc = 0; doc < atomicReader.MaxDoc; doc++)
            {
                if (acceptedIterator != null)
                {
                    // The iterator only ever moves forward, so walk it in step with the scan
                    // rather than restarting it per document.
                    while (nextAccepted < doc && nextAccepted != DocIdSetIterator.NO_MORE_DOCS)
                    {
                        nextAccepted = acceptedIterator.Advance(doc);
                    }

                    if (nextAccepted != doc)
                    {
                        continue;
                    }
                }
                else if (acceptedBits?.Get(doc) == false)
                {
                    continue;
                }
                else if (acceptedBits == null && liveDocs?.Get(doc) == false)
                {
                    // Without a filter, deleted documents still have to be skipped; with one,
                    // the filter was already given liveDocs to honour.
                    continue;
                }

                if (!TryScore(readers, doc, buffer, bytes, out var score))
                {
                    continue;
                }

                var globalDoc = context.DocBase + doc;

                if (best.Count < _k)
                {
                    best.Enqueue(globalDoc, score);
                }
                else if (best.TryPeek(out _, out var worst) && score > worst)
                {
                    best.Dequeue();
                    best.Enqueue(globalDoc, score);
                }
            }
        }

        var hits = new Dictionary<int, float>(best.Count);

        while (best.TryDequeue(out var doc, out var score))
        {
            hits[doc] = score;
        }

        return hits;
    }

    /// <summary>
    /// Scores one document against the query vector, taking its best field.
    /// </summary>
    /// <remarks>
    /// Returns false for a document that holds no vector for any of the named fields. Azure
    /// leaves such a document out of the results rather than ranking it last, since it has no
    /// similarity to the query at all — absence is not distance.
    ///
    /// A stored vector of the wrong length is also skipped rather than reported. Validation
    /// refuses a mismatched vector at upload and a mismatched query vector before the scan, so
    /// reaching here means the field's dimensions changed under documents already written —
    /// which the schema rules prevent — and failing the whole query over one stale document
    /// would be a worse answer than omitting it.
    /// </remarks>
    private bool TryScore(
        List<BinaryDocValues> readers,
        int doc,
        float[] buffer,
        BytesRef bytes,
        out float score)
    {
        score = float.NegativeInfinity;
        var found = false;

        foreach (var reader in readers)
        {
            reader.Get(doc, bytes);

            if (bytes.Length == 0 || bytes.Length != buffer.Length * sizeof(float))
            {
                continue;
            }

            VectorSearchSupport.UnpackVector(bytes.Bytes.AsSpan(bytes.Offset, bytes.Length), buffer);

            var candidate = VectorSimilarity.GetScore(
                _metric,
                VectorSimilarity.GetSimilarity(_metric, buffer, _vector));

            if (!found || candidate > score)
            {
                score = candidate;
                found = true;
            }
        }

        return found;
    }

    public override string ToString(string field)
        => $"vector({string.Join(",", _paths)}, k={_k}, metric={_metric})";

    /// <remarks>
    /// The query vector participates in identity: two queries over the same field with
    /// different vectors select different documents, so they are not the same query and must
    /// not share a cache entry.
    /// </remarks>
    public override bool Equals(object? obj)
        => obj is VectorSearchQuery other
           && _paths.SequenceEqual(other._paths)
           && _vector.AsSpan().SequenceEqual(other._vector)
           && _metric == other._metric
           && _k == other._k
           && Equals(_preFilter, other._preFilter)
           && Boost.Equals(other.Boost);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var path in _paths)
        {
            hash.Add(path);
        }

        foreach (var component in _vector)
        {
            hash.Add(component);
        }

        hash.Add(_metric);
        hash.Add(_k);
        hash.Add(Boost);

        return hash.ToHashCode();
    }
}
