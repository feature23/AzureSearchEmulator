using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the arithmetic of a scoring profile: the shape of each interpolation, the
/// range rules of each function, and how several functions combine (issue #47).
/// </summary>
/// <remarks>
/// These assert on relationships — this boost exceeds that one, this value sits at the range's
/// end — rather than on exact constants wherever the exact constant is not something Azure
/// documents. See <see cref="ScoringFunctionEvaluator"/> for why the curves cannot be pinned
/// more precisely than that, and why relative ordering is the thing worth getting right.
/// </remarks>
public class ScoringProfileTests
{
    [Theory]
    [InlineData(ScoringFunctionInterpolation.Linear)]
    [InlineData(ScoringFunctionInterpolation.Quadratic)]
    [InlineData(ScoringFunctionInterpolation.Logarithmic)]
    public void Interpolation_IsFullBoostAtStartAndNoneAtEnd(ScoringFunctionInterpolation interpolation)
    {
        // Every curve spans the same two endpoints; only the path between them differs.
        Assert.Equal(3.0, ScoringFunctionEvaluator.Interpolate(3.0, 0, interpolation), 5);
        Assert.Equal(1.0, ScoringFunctionEvaluator.Interpolate(3.0, 1, interpolation), 5);
    }

    /// <summary>
    /// Constant is the exception: it holds the full boost across the whole range rather than
    /// decaying over it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void ConstantInterpolation_HoldsTheBoostAcrossTheRange(double position)
    {
        Assert.Equal(
            3.0,
            ScoringFunctionEvaluator.Interpolate(3.0, position, ScoringFunctionInterpolation.Constant),
            5);
    }

    [Theory]
    [InlineData(ScoringFunctionInterpolation.Linear)]
    [InlineData(ScoringFunctionInterpolation.Quadratic)]
    [InlineData(ScoringFunctionInterpolation.Logarithmic)]
    public void Interpolation_DecreasesAcrossTheRange(ScoringFunctionInterpolation interpolation)
    {
        // "Because scoring is high to low, the slope is always decreasing" — the one property
        // Azure states unambiguously about all three curves.
        var previous = double.MaxValue;

        for (var t = 0.0; t <= 1.0; t += 0.05)
        {
            var boost = ScoringFunctionEvaluator.Interpolate(3.0, t, interpolation);

            Assert.True(boost <= previous, $"boost rose at t={t}");
            previous = boost;
        }
    }

    /// <summary>
    /// The two curves bracket the linear one from opposite sides, which is what makes them
    /// distinguishable choices rather than three spellings of the same decay.
    /// </summary>
    [Fact]
    public void QuadraticHoldsHigherThanLinear_AndLogarithmicLower()
    {
        const double midpoint = 0.5;

        var quadratic = ScoringFunctionEvaluator.Interpolate(3.0, midpoint, ScoringFunctionInterpolation.Quadratic);
        var linear = ScoringFunctionEvaluator.Interpolate(3.0, midpoint, ScoringFunctionInterpolation.Linear);
        var logarithmic = ScoringFunctionEvaluator.Interpolate(3.0, midpoint, ScoringFunctionInterpolation.Logarithmic);

        Assert.True(logarithmic < linear, "logarithmic should fall away faster than linear");
        Assert.True(quadratic > linear, "quadratic should hold its value longer than linear");
    }

    [Fact]
    public void Interpolation_ClampsOutsideTheRange()
    {
        // Callers pass a raw ratio, so a value past either end must not run the curve past its
        // endpoints and produce a boost below 1 or above the configured maximum.
        Assert.Equal(3.0, ScoringFunctionEvaluator.Interpolate(3.0, -0.5, ScoringFunctionInterpolation.Linear), 5);
        Assert.Equal(1.0, ScoringFunctionEvaluator.Interpolate(3.0, 1.5, ScoringFunctionInterpolation.Linear), 5);
    }

    [Fact]
    public void Magnitude_BoostsLargerValuesTowardTheRangeStart()
    {
        var function = Magnitude(0, 100, boost: 3);

        var low = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 0);
        var high = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 100);

        // The range start is the strong end, so a value there gets the full boost.
        Assert.Equal(3.0, low!.Value, 5);
        Assert.Equal(1.0, high!.Value, 5);
    }

    /// <summary>
    /// A range running downward is how Azure documents boosting cheaper items over dearer ones.
    /// </summary>
    [Fact]
    public void Magnitude_ReversedRange_BoostsSmallerValues()
    {
        var function = Magnitude(100, 1, boost: 3);

        var cheap = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 1);
        var dear = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 100);

        Assert.True(cheap < dear, "a reversed range should boost the low end least");
    }

    [Fact]
    public void Magnitude_OutsideTheRange_DoesNotApply()
    {
        var function = Magnitude(10, 100, boost: 3);

        // Short of the start, and past the end without the constant-boost flag.
        Assert.Null(ScoringFunctionEvaluator.GetMagnitudeBoost(function, 5));
        Assert.Null(ScoringFunctionEvaluator.GetMagnitudeBoost(function, 500));
    }

    [Fact]
    public void Magnitude_ConstantBoostBeyondRange_KeepsTheEndBoost()
    {
        var function = Magnitude(10, 100, boost: 3);
        function.Magnitude!.ConstantBoostBeyondRange = true;

        var beyond = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 500);
        var atEnd = ScoringFunctionEvaluator.GetMagnitudeBoost(function, 100);

        Assert.Equal(atEnd!.Value, beyond!.Value, 5);
    }

    [Fact]
    public void Freshness_BoostsRecentDocumentsMost()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var function = Freshness("P365D", boost: 3);

        var today = ScoringFunctionEvaluator.GetFreshnessBoost(function, now, now);
        var halfYear = ScoringFunctionEvaluator.GetFreshnessBoost(function, now.AddDays(-182), now);

        Assert.Equal(3.0, today!.Value, 5);
        Assert.True(halfYear < today, "an older document should be boosted less");
    }

    [Fact]
    public void Freshness_BeyondTheDuration_DoesNotApply()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var function = Freshness("P30D", boost: 3);

        Assert.Null(ScoringFunctionEvaluator.GetFreshnessBoost(function, now.AddDays(-31), now));
    }

    /// <summary>
    /// A negative duration turns the function around to face the future, which is how an
    /// upcoming event is promoted over a more distant one.
    /// </summary>
    [Fact]
    public void Freshness_NegativeDuration_BoostsFutureDates()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var function = Freshness("-P30D", boost: 3);

        var soon = ScoringFunctionEvaluator.GetFreshnessBoost(function, now.AddDays(1), now);
        var later = ScoringFunctionEvaluator.GetFreshnessBoost(function, now.AddDays(20), now);

        Assert.NotNull(soon);
        Assert.True(soon > later, "a nearer future date should be boosted more");

        // A past date is outside a forward-facing function entirely.
        Assert.Null(ScoringFunctionEvaluator.GetFreshnessBoost(function, now.AddDays(-1), now));
    }

    [Fact]
    public void Distance_BoostsNearerDocumentsMost()
    {
        var function = Distance(boostingDistance: 10, boost: 3);

        Assert.Equal(3.0, ScoringFunctionEvaluator.GetDistanceBoost(function, 0)!.Value, 5);
        Assert.True(ScoringFunctionEvaluator.GetDistanceBoost(function, 5) < 3.0);

        // Unlike magnitude, a distance function has no way to hold its boost past the range.
        Assert.Null(ScoringFunctionEvaluator.GetDistanceBoost(function, 11));
    }

    [Fact]
    public void Tag_BoostsMoreMatchesHigher()
    {
        var function = Tag(boost: 3);

        var one = ScoringFunctionEvaluator.GetTagBoost(function, matchedTags: 1, requestedTags: 2);
        var both = ScoringFunctionEvaluator.GetTagBoost(function, matchedTags: 2, requestedTags: 2);

        Assert.True(one < both, "matching more of the requested tags should boost further");

        // Matching everything asked for is the full boost, so the single-tag case — much the
        // most common — is unaffected by the proportional scaling.
        Assert.Equal(3.0, both!.Value, 5);
        Assert.Equal(3.0, ScoringFunctionEvaluator.GetTagBoost(function, 1, 1)!.Value, 5);
    }

    [Fact]
    public void Tag_NoMatch_DoesNotApply()
    {
        Assert.Null(ScoringFunctionEvaluator.GetTagBoost(Tag(boost: 3), matchedTags: 0, requestedTags: 2));
    }

    [Theory]
    [InlineData(ScoringFunctionAggregation.Sum, 6)]
    [InlineData(ScoringFunctionAggregation.Average, 2)]
    [InlineData(ScoringFunctionAggregation.Minimum, 1)]
    [InlineData(ScoringFunctionAggregation.Maximum, 3)]
    [InlineData(ScoringFunctionAggregation.FirstMatching, 1)]
    [InlineData(ScoringFunctionAggregation.Product, 6)]
    public void Aggregate_CombinesBoosts(ScoringFunctionAggregation aggregation, double expected)
    {
        Assert.Equal(expected, ScoringFunctionEvaluator.Aggregate([1, 2, 3], aggregation), 5);
    }

    /// <summary>
    /// A document no function applies to keeps the score it already had, which also keeps
    /// <c>average</c> from dividing by zero.
    /// </summary>
    [Theory]
    [InlineData(ScoringFunctionAggregation.Sum)]
    [InlineData(ScoringFunctionAggregation.Average)]
    [InlineData(ScoringFunctionAggregation.Minimum)]
    [InlineData(ScoringFunctionAggregation.Product)]
    public void Aggregate_WithNoMatchingFunctions_LeavesTheScoreAlone(ScoringFunctionAggregation aggregation)
    {
        Assert.Equal(1.0, ScoringFunctionEvaluator.Aggregate([], aggregation), 5);
    }

    private static MagnitudeScoringFunction Magnitude(double start, double end, double boost) =>
        new()
        {
            FieldName = "Rating",
            Boost = boost,
            Magnitude = new MagnitudeScoringParameters
            {
                BoostingRangeStart = start,
                BoostingRangeEnd = end,
            },
        };

    private static FreshnessScoringFunction Freshness(string duration, double boost) =>
        new()
        {
            FieldName = "Created",
            Boost = boost,
            Freshness = new FreshnessScoringParameters { BoostingDuration = duration },
        };

    private static DistanceScoringFunction Distance(double boostingDistance, double boost) =>
        new()
        {
            FieldName = "Location",
            Boost = boost,
            Distance = new DistanceScoringParameters
            {
                ReferencePointParameter = "here",
                BoostingDistance = boostingDistance,
            },
        };

    private static TagScoringFunction Tag(double boost) =>
        new()
        {
            FieldName = "Tags",
            Boost = boost,
            Tag = new TagScoringParameters { TagsParameter = "mytags" },
        };
}
