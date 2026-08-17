using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// The arithmetic of a scoring profile's functions: how one function's boost is shaped across
/// its range, and how several functions combine into the multiplier applied to a document's
/// score (issue #47).
/// </summary>
/// <remarks>
/// Kept apart from the Lucene plumbing that feeds it so the shape of each curve can be tested
/// directly on numbers, without an index in the way.
///
/// <para><b>On exactness.</b> Azure documents the interpolations only qualitatively — "boosts
/// scores by an amount that decreases quadratically" — and never publishes the formulas. Worse,
/// its prose contradicts itself on which of quadratic and logarithmic falls off faster near the
/// start of the range: the how-to says quadratic "tapers off more slowly at the far end" while
/// the reference says its boosts "decrease slowly for higher scores, and more quickly as the
/// scores decrease". The curves here follow the reference, which describes each value
/// individually rather than comparing them in passing. What is documented and consistent is
/// the frame:</para>
/// <list type="bullet">
/// <item>the boost is a multiplier on the score the document would otherwise get;</item>
/// <item>it is strongest at the near end of the range — the largest magnitude, the newest date,
/// distance zero — and decays toward the far end;</item>
/// <item>past the far end there is no boost, i.e. a multiplier of 1.</item>
/// </list>
/// <para>The curves below are the standard reading of that frame: every one starts at
/// <c>boost</c> when the value sits at the near end and reaches 1 at the far end, differing only
/// in the shape between. Relative ordering — which documents outrank which — follows the frame
/// rather than the exact curve, and ordering is what a scoring profile is written to control.
/// Absolute scores already differ from Azure's because the underlying relevance implementations
/// differ, so pinning the curve to more precision than the documentation supports would buy
/// nothing real. The README says so plainly rather than implying a parity that does not
/// exist.</para>
/// </remarks>
public static class ScoringFunctionEvaluator
{
    /// <summary>
    /// The multiplier for a document no function boosted, which leaves the score untouched.
    /// </summary>
    public const double NoBoost = 1.0;

    /// <summary>
    /// Shapes a boost across a function's range.
    /// </summary>
    /// <param name="boost">The multiplier at the near end of the range.</param>
    /// <param name="distanceFromStart">
    /// How far along the range the document's value sits, as a fraction: 0 at the near end,
    /// 1 at the far end. Values outside 0..1 are the caller's signal that the document is
    /// outside the range, and are clamped here.
    /// </param>
    /// <param name="interpolation">The curve to apply.</param>
    public static double Interpolate(
        double boost,
        double distanceFromStart,
        ScoringFunctionInterpolation interpolation)
    {
        var t = Math.Clamp(distanceFromStart, 0, 1);

        // How much of the boost remains at this point in the range, from 1 at the near end
        // down to 0 at the far end.
        var remaining = interpolation switch
        {
            // Full boost anywhere inside the range, dropping off only at its edge.
            ScoringFunctionInterpolation.Constant => 1.0,

            ScoringFunctionInterpolation.Linear => 1.0 - t,

            // "Boosts decrease slowly for higher scores, and more quickly as the scores
            // decrease": the boost holds near the strong end of the range and falls away
            // sharply toward the weak end, so it sits above the linear line throughout.
            ScoringFunctionInterpolation.Quadratic => 1.0 - t * t,

            // "Boosts decrease quickly for higher scores, and more slowly as the scores
            // decrease": the mirror image, dropping steeply at first and then flattening, so it
            // sits below the linear line. Expressed through a log that is exactly 1 at t = 0 and
            // exactly 0 at t = 1, with no singularity at either end.
            ScoringFunctionInterpolation.Logarithmic => 1.0 - Math.Log(1.0 + (Math.E - 1.0) * t),

            _ => 1.0 - t,
        };

        return 1.0 + (boost - 1.0) * Math.Clamp(remaining, 0, 1);
    }

    /// <summary>
    /// Combines the boosts of the functions that matched a document into the single multiplier
    /// applied to its score.
    /// </summary>
    /// <remarks>
    /// Only the functions that actually apply to the document are passed in: a function whose
    /// field the document leaves null, or whose range the value falls outside of, contributes
    /// nothing rather than contributing 1. The distinction matters for every aggregation except
    /// <c>sum</c> — an <c>average</c> that included the non-matching functions would be dragged
    /// toward 1 by fields the document does not even have, and a <c>minimum</c> would be pinned
    /// at 1 by the first such field.
    ///
    /// With nothing matching, the result is <see cref="NoBoost"/>: Azure describes
    /// <c>functionAggregation</c> as ignored when there are no scoring functions, and a document
    /// no function applies to is the same case seen per-document. It also keeps <c>average</c>
    /// from dividing by zero.
    /// </remarks>
    public static double Aggregate(IReadOnlyList<double> boosts, ScoringFunctionAggregation aggregation)
    {
        if (boosts.Count == 0)
        {
            return NoBoost;
        }

        return aggregation switch
        {
            ScoringFunctionAggregation.Sum => boosts.Sum(),
            ScoringFunctionAggregation.Average => boosts.Average(),
            ScoringFunctionAggregation.Minimum => boosts.Min(),
            ScoringFunctionAggregation.Maximum => boosts.Max(),
            // The functions are evaluated in the order the profile declares them, so the first
            // that applied to this document is the first in the list.
            ScoringFunctionAggregation.FirstMatching => boosts[0],
            ScoringFunctionAggregation.Product => boosts.Aggregate(1.0, (a, b) => a * b),
            _ => boosts.Sum(),
        };
    }

    /// <summary>
    /// Boost for a <c>magnitude</c> function, from how far the value sits along the range.
    /// </summary>
    /// <remarks>
    /// The range may run downward — <c>boostingRangeStart</c> above <c>boostingRangeEnd</c> —
    /// which is how Azure documents boosting cheaper items over dearer ones. Normalizing by the
    /// signed span handles both directions without a special case.
    ///
    /// Returns null when the function does not apply to this value, which is the case below the
    /// start of the range, and above its end unless <c>constantBoostBeyondRange</c> asks for the
    /// boost to be held.
    /// </remarks>
    public static double? GetMagnitudeBoost(MagnitudeScoringFunction function, double value)
    {
        var parameters = function.Magnitude;

        if (parameters == null)
        {
            return null;
        }

        var span = parameters.BoostingRangeEnd - parameters.BoostingRangeStart;

        if (span == 0)
        {
            return null;
        }

        var t = (value - parameters.BoostingRangeStart) / span;

        if (t < 0)
        {
            // Short of the start of the range: outside the function's reach entirely.
            return null;
        }

        if (t > 1)
        {
            return parameters.ConstantBoostBeyondRange
                ? Interpolate(function.Boost, 1, function.Interpolation)
                : null;
        }

        return Interpolate(function.Boost, t, function.Interpolation);
    }

    /// <summary>
    /// Boost for a <c>freshness</c> function, from a document's date and the time the query ran.
    /// </summary>
    /// <remarks>
    /// A positive <c>boostingDuration</c> boosts documents in the interval reaching back from
    /// now, most strongly at the present. A negative one mirrors it into the future, which is how
    /// upcoming events are promoted; the age is negated so the same interpolation applies from
    /// the near end outward in both cases.
    ///
    /// Returns null for a document outside the interval, in either direction.
    /// </remarks>
    public static double? GetFreshnessBoost(
        FreshnessScoringFunction function,
        DateTimeOffset value,
        DateTimeOffset now)
    {
        var duration = function.Freshness?.BoostingDuration;

        if (string.IsNullOrEmpty(duration))
        {
            return null;
        }

        TimeSpan span;

        try
        {
            span = ScoringProfileJson.ParseDuration(duration);
        }
        catch (FormatException)
        {
            // A malformed duration is refused when the index is defined, so reaching this at
            // query time would mean a definition written before that check; scoring nothing is
            // the safe reading.
            return null;
        }

        if (span == TimeSpan.Zero)
        {
            return null;
        }

        // Positive for a document in the past, negative for one in the future.
        var age = now - value;

        // A negative duration turns the function around to face the future, so both cases
        // reduce to "how far from now, in the direction the duration points".
        if (span < TimeSpan.Zero)
        {
            age = -age;
            span = -span;
        }

        if (age < TimeSpan.Zero || age > span)
        {
            return null;
        }

        return Interpolate(function.Boost, age / span, function.Interpolation);
    }

    /// <summary>
    /// Boost for a <c>distance</c> function, from a document's distance in kilometers to the
    /// reference point.
    /// </summary>
    /// <remarks>
    /// Returns null past <c>boostingDistance</c>. Unlike <c>magnitude</c>, a distance function
    /// has no option to hold the boost beyond its range.
    /// </remarks>
    public static double? GetDistanceBoost(DistanceScoringFunction function, double distanceKm)
    {
        var boostingDistance = function.Distance?.BoostingDistance ?? 0;

        if (boostingDistance <= 0 || distanceKm > boostingDistance)
        {
            return null;
        }

        return Interpolate(function.Boost, distanceKm / boostingDistance, function.Interpolation);
    }

    /// <summary>
    /// Boost for a <c>tag</c> function, from how many of the caller's tags the document carries.
    /// </summary>
    /// <remarks>
    /// Azure documents only that a document is boosted "if any item in the collection is
    /// matched", and does not say whether matching more tags boosts further. Scaling by the
    /// proportion matched is the reading that keeps a document matching every tag ahead of one
    /// matching a single tag, which is the ordering a caller supplying several tags is asking
    /// for; matching all of them gives the full boost, so the single-tag case — much the most
    /// common — behaves identically either way.
    ///
    /// This is also why tag functions may not use quadratic or logarithmic interpolation: the
    /// position along the range is a proportion of tags, not a field value, and Azure refuses
    /// the curves that would shape it. See <see cref="Indexing.ScoringProfileValidator"/>.
    ///
    /// Returns null when the document carries none of the tags.
    /// </remarks>
    public static double? GetTagBoost(TagScoringFunction function, int matchedTags, int requestedTags)
    {
        if (matchedTags <= 0 || requestedTags <= 0)
        {
            return null;
        }

        var proportion = Math.Min(matchedTags, requestedTags) / (double)requestedTags;

        // The proportion runs the other way from a range position: matching everything is the
        // strongest outcome, which is the near end of the range.
        return Interpolate(function.Boost, 1 - proportion, function.Interpolation);
    }
}
