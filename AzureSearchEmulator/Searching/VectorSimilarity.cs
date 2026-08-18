using System.Numerics.Tensors;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// The similarity metrics a vector query ranks by, and the transform from a metric's natural
/// value to the <c>@search.score</c> a response reports (issue #46).
/// </summary>
/// <remarks>
/// <para>
/// Two different numbers are involved and it is worth keeping them apart. The <em>similarity</em>
/// is the metric's own value — a cosine similarity in [-1, 1], a dot product on any scale, a
/// Euclidean distance in [0, ∞). The <em>score</em> is what Azure puts in <c>@search.score</c>,
/// and it is not the same number: it is always higher-is-better, which a distance is not.
/// </para>
/// <para>
/// Only the cosine transform is documented. Azure publishes
/// <c>score = 1 / (1 + cosine_distance)</c>, where the distance is <c>1 - cosine_similarity</c>,
/// and that is implemented here exactly. For the other two metrics Microsoft confirms a
/// transform exists without publishing it — the REST specification says only that "vector
/// similarity is related to <c>@search.score</c> by an equation" — so the transforms below are
/// the emulator's own, chosen to be monotonic in the metric and to land in the same
/// <c>(0, 1]</c> range the documented one does.
/// </para>
/// <para>
/// The consequence is worth stating plainly: <strong>ordering is faithful for every metric,
/// and the absolute score is faithful only for cosine.</strong> A test that asserts which
/// documents come back and in what order will agree with the service; one that asserts a
/// literal score for <c>dotProduct</c> or <c>euclidean</c> may not. This is the same position
/// <see cref="ScoringFunctionEvaluator"/> takes, and for the same reason — the emulator can
/// reproduce the behaviour that is specified, not the behaviour that is not.
/// </para>
/// </remarks>
public static class VectorSimilarity
{
    /// <summary>
    /// Computes the raw similarity of two vectors under <paramref name="metric"/>.
    /// </summary>
    /// <remarks>
    /// Higher is closer for cosine and dot product; lower is closer for Euclidean, which is a
    /// distance. Use <see cref="GetScore"/> to get a value that is higher-is-better for all
    /// three.
    /// </remarks>
    public static float GetSimilarity(VectorSearchMetric metric, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => metric switch
        {
            VectorSearchMetric.DotProduct => TensorPrimitives.Dot(a, b),
            VectorSearchMetric.Euclidean => TensorPrimitives.Distance(a, b),
            _ => GetCosineSimilarity(a, b)
        };

    /// <summary>
    /// Converts a raw similarity into the higher-is-better value <c>@search.score</c> reports.
    /// </summary>
    public static float GetScore(VectorSearchMetric metric, float similarity)
        => metric switch
        {
            // Documented by Azure: score = 1 / (1 + cosine_distance), cosine_distance being
            // 1 - cosine_similarity. Yields (0, 1], reaching 1 for identical direction.
            VectorSearchMetric.Cosine => Squash(1f - similarity),

            // Not documented. A Euclidean distance is already in [0, ∞) and already
            // lower-is-better, so the cosine transform's shape applies unchanged.
            VectorSearchMetric.Euclidean => Squash(similarity),

            // Not documented, and the least constrained of the three: a dot product is
            // unbounded in both directions, so there is no distance to invert. The reciprocal
            // curve the other two use cannot be reused here — it is only monotonic for inputs
            // above -1, and a dot product routinely exceeds that, where the curve turns and
            // starts ranking more similar vectors lower. This algebraic sigmoid is strictly
            // increasing across the whole real line and stays inside (0, 1), which keeps the
            // ordering faithful — the part that is worth being faithful to.
            _ => Sigmoid(similarity)
        };

    /// <summary>
    /// Computes similarity and score together, which is what a scan actually needs.
    /// </summary>
    public static (float Similarity, float Score) Evaluate(
        VectorSearchMetric metric,
        ReadOnlySpan<float> a,
        ReadOnlySpan<float> b)
    {
        var similarity = GetSimilarity(metric, a, b);

        return (similarity, GetScore(metric, similarity));
    }

    /// <summary>
    /// Maps a lower-is-better quantity onto <c>(0, 1]</c>, the shape Azure's documented cosine
    /// transform has.
    /// </summary>
    /// <remarks>
    /// Guards the two ways the arithmetic can leave that range. A NaN input — which
    /// <see cref="TensorPrimitives"/> produces for a zero-length vector under cosine, since the
    /// magnitude it divides by is zero — would otherwise propagate into the score and make the
    /// document unorderable against every other; it is reported as the lowest score instead. A
    /// value below -1 (possible for an unbounded dot product) would drive the denominator
    /// through zero and flip the sign, so the denominator is held just above zero.
    /// </remarks>
    private static float Squash(float distance)
    {
        if (float.IsNaN(distance))
        {
            return 0f;
        }

        var denominator = 1f + distance;

        return denominator <= float.Epsilon ? float.MaxValue : 1f / denominator;
    }

    /// <summary>
    /// Maps an unbounded higher-is-better quantity onto <c>(0, 1)</c>, strictly increasing.
    /// </summary>
    /// <remarks>
    /// The algebraic sigmoid <c>½(1 + s / (1 + |s|))</c>, chosen over the logistic one because
    /// it needs no exponential and cannot saturate to exactly 0 or 1 for any finite input, so
    /// two documents with different similarities never come out with the same score. Infinities
    /// are mapped to the ends of the range, and NaN to the bottom, so that every document stays
    /// orderable.
    /// </remarks>
    private static float Sigmoid(float similarity)
    {
        if (float.IsNaN(similarity))
        {
            return 0f;
        }

        if (float.IsPositiveInfinity(similarity))
        {
            return 1f;
        }

        if (float.IsNegativeInfinity(similarity))
        {
            return 0f;
        }

        return 0.5f * (1f + similarity / (1f + MathF.Abs(similarity)));
    }

    /// <summary>
    /// Cosine similarity, defined as 0 when either vector has no magnitude.
    /// </summary>
    /// <remarks>
    /// <see cref="TensorPrimitives.CosineSimilarity{T}"/> divides by the product of the
    /// magnitudes and so returns NaN for a zero vector. A zero vector has no direction, which
    /// makes it no closer to one query than another, so 0 — orthogonal — is the answer that
    /// keeps it orderable and ranked below anything with real direction.
    /// </remarks>
    private static float GetCosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var similarity = TensorPrimitives.CosineSimilarity(a, b);

        return float.IsNaN(similarity) ? 0f : similarity;
    }
}
