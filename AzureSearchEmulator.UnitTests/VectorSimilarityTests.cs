using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the similarity metrics and the transform to <c>@search.score</c> (issue #46).
/// </summary>
/// <remarks>
/// Only the cosine transform is documented by Azure, so only cosine is asserted against a
/// literal score. The other two metrics are asserted on ordering, which is the part the emulator
/// undertakes to reproduce — see <see cref="VectorSimilarity"/>.
/// </remarks>
public class VectorSimilarityTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void Cosine_IsOneForIdenticalDirection()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.Cosine, [1f, 0f, 0f], [5f, 0f, 0f]);

        Assert.Equal(1f, similarity, Tolerance);
    }

    [Fact]
    public void Cosine_IsZeroForOrthogonal()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.Cosine, [1f, 0f], [0f, 1f]);

        Assert.Equal(0f, similarity, Tolerance);
    }

    [Fact]
    public void Cosine_IsMinusOneForOpposite()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.Cosine, [1f, 0f], [-1f, 0f]);

        Assert.Equal(-1f, similarity, Tolerance);
    }

    /// <summary>
    /// Azure documents <c>score = 1 / (1 + cosine_distance)</c>, so this is the one metric whose
    /// absolute score the emulator can claim to reproduce.
    /// </summary>
    [Theory]
    [InlineData(1f, 1f)]          // distance 0 -> 1 / 1
    [InlineData(0f, 0.5f)]        // distance 1 -> 1 / 2
    [InlineData(-1f, 1f / 3f)]    // distance 2 -> 1 / 3
    [InlineData(0.5f, 1f / 1.5f)] // distance 0.5 -> 1 / 1.5
    public void Cosine_ScoreMatchesAzuresDocumentedFormula(float similarity, float expected)
    {
        Assert.Equal(expected, VectorSimilarity.GetScore(VectorSearchMetric.Cosine, similarity), Tolerance);
    }

    [Fact]
    public void DotProduct_IsTheDotProduct()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.DotProduct, [1f, 2f, 3f], [4f, 5f, 6f]);

        Assert.Equal(32f, similarity, Tolerance);
    }

    [Fact]
    public void Euclidean_IsTheDistance()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.Euclidean, [0f, 0f], [3f, 4f]);

        Assert.Equal(5f, similarity, Tolerance);
    }

    /// <summary>
    /// The score has to be higher-is-better for every metric, including the one whose raw value
    /// is a distance. This is the property the ranking depends on.
    /// </summary>
    [Theory]
    [InlineData(VectorSearchMetric.Cosine, 1f, 0f)]        // closer, further (similarity)
    [InlineData(VectorSearchMetric.DotProduct, 10f, 2f)]   // closer, further (similarity)
    [InlineData(VectorSearchMetric.Euclidean, 1f, 9f)]     // closer, further (distance)
    public void Score_IsAlwaysHigherIsBetter(VectorSearchMetric metric, float closer, float further)
    {
        var closerScore = VectorSimilarity.GetScore(metric, closer);
        var furtherScore = VectorSimilarity.GetScore(metric, further);

        Assert.True(
            closerScore > furtherScore,
            $"{metric}: expected the nearer vector to score higher, got {closerScore} vs {furtherScore}");
    }

    /// <summary>
    /// Every metric's score lands in the range the documented cosine transform produces, so a
    /// caller comparing scores across metrics is at least comparing numbers of the same shape.
    /// </summary>
    [Theory]
    [InlineData(VectorSearchMetric.Cosine, -1f)]
    [InlineData(VectorSearchMetric.Cosine, 1f)]
    [InlineData(VectorSearchMetric.Euclidean, 0f)]
    [InlineData(VectorSearchMetric.Euclidean, 1000f)]
    [InlineData(VectorSearchMetric.DotProduct, -50f)]
    [InlineData(VectorSearchMetric.DotProduct, 0f)]
    public void Score_IsInZeroToOne(VectorSearchMetric metric, float similarity)
    {
        var score = VectorSimilarity.GetScore(metric, similarity);

        Assert.InRange(score, 0f, 1f);
    }

    /// <summary>
    /// A zero vector has no direction, so cosine cannot divide by its magnitude. Left as NaN it
    /// would make the document unorderable against every other; 0 ranks it below anything with
    /// real direction, which is the sensible reading of "no direction at all".
    /// </summary>
    [Fact]
    public void Cosine_OfZeroVector_IsZeroRatherThanNaN()
    {
        var similarity = VectorSimilarity.GetSimilarity(VectorSearchMetric.Cosine, [0f, 0f], [1f, 1f]);

        Assert.False(float.IsNaN(similarity));
        Assert.Equal(0f, similarity, Tolerance);
    }

    [Fact]
    public void Score_OfNaN_IsFiniteAndLowest()
    {
        var score = VectorSimilarity.GetScore(VectorSearchMetric.Cosine, float.NaN);

        Assert.False(float.IsNaN(score));
        Assert.Equal(0f, score);
    }

    /// <summary>
    /// A dot product is unbounded, so a large enough one would otherwise drive the transform's
    /// denominator through zero and flip the score's sign.
    /// </summary>
    [Fact]
    public void DotProduct_ScoreStaysFiniteAndPositive_ForLargeSimilarity()
    {
        var score = VectorSimilarity.GetScore(VectorSearchMetric.DotProduct, 1e30f);

        Assert.True(float.IsFinite(score));
        Assert.True(score > 0f);
    }

    /// <summary>
    /// The ordering property has to hold across each metric's whole natural domain, not just
    /// near the values a well-behaved embedding produces. A transform that is monotonic over
    /// part of its range and turns over the rest ranks more similar vectors lower, which is the
    /// worst kind of wrong: plausible results in the wrong order.
    /// </summary>
    /// <remarks>
    /// A dot product in particular is unbounded, so it regularly reaches values where a curve
    /// fitted to a bounded metric stops behaving.
    /// </remarks>
    [Theory]
    [InlineData(VectorSearchMetric.Cosine)]
    [InlineData(VectorSearchMetric.DotProduct)]
    [InlineData(VectorSearchMetric.Euclidean)]
    public void Score_IsMonotonicAcrossTheMetricsRange(VectorSearchMetric metric)
    {
        // Cosine is bounded to [-1, 1]; the other two are not, so they are probed far wider.
        float[] similarities = metric == VectorSearchMetric.Cosine
            ? [-1f, -0.75f, -0.5f, -0.25f, 0f, 0.25f, 0.5f, 0.75f, 1f]
            : [-1e6f, -1000f, -100f, -10f, -2f, -1f, -0.5f, 0f, 0.5f, 1f, 2f, 10f, 100f, 1000f, 1e6f];

        // Euclidean is a distance, so "better" runs the other way and the sequence is reversed
        // to put the nearest first either way.
        var ordered = metric == VectorSearchMetric.Euclidean
            ? similarities.Where(i => i >= 0f).OrderBy(i => i).ToArray()
            : similarities.OrderByDescending(i => i).ToArray();

        var scores = ordered.Select(i => VectorSimilarity.GetScore(metric, i)).ToArray();

        for (var i = 1; i < scores.Length; i++)
        {
            Assert.True(
                scores[i] < scores[i - 1],
                $"{metric}: similarity {ordered[i]} scored {scores[i]}, which is not below " +
                $"the {scores[i - 1]} scored by the nearer {ordered[i - 1]}");
        }
    }

    /// <summary>
    /// A similarity marginally above 1 — reachable from floating-point accumulation over
    /// near-identical vectors — makes the cosine distance marginally negative. The score is
    /// clamped to 1 rather than allowed to run away: one document reported at 3.4e38 among
    /// neighbours scoring near 1 is far more confusing than one reported as the perfect match it
    /// very nearly is.
    /// </summary>
    [Theory]
    [InlineData(1.0000001f)]
    [InlineData(1.001f)]
    [InlineData(2f)]
    public void Cosine_ScoreIsClamped_ForSimilarityAboveOne(float similarity)
    {
        var score = VectorSimilarity.GetScore(VectorSearchMetric.Cosine, similarity);

        Assert.InRange(score, 0f, 1f);
    }

    /// <summary>
    /// A negative Euclidean distance is not physically meaningful, but the transform must stay
    /// inside its documented range if one ever arrives.
    /// </summary>
    [Fact]
    public void Euclidean_ScoreIsClamped_ForNegativeDistance()
    {
        Assert.InRange(VectorSimilarity.GetScore(VectorSearchMetric.Euclidean, -2f), 0f, 1f);
    }

    [Fact]
    public void Evaluate_ReturnsBothHalves()
    {
        var (similarity, score) = VectorSimilarity.Evaluate(VectorSearchMetric.Cosine, [1f, 0f], [1f, 0f]);

        Assert.Equal(1f, similarity, Tolerance);
        Assert.Equal(1f, score, Tolerance);
    }
}
