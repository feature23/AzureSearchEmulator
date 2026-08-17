using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the index-time validation of scoring profiles and for parsing the
/// <c>scoringParameter</c> values a query supplies (issue #47).
/// </summary>
public class ScoringProfileValidationTests
{
    [Fact]
    public void ValidProfile_IsAccepted()
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Text = new TextWeights { Weights = { ["Name"] = 3 } },
            Functions =
            [
                new MagnitudeScoringFunction
                {
                    FieldName = "Rating",
                    Boost = 2,
                    Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 0, BoostingRangeEnd = 5 },
                }
            ],
        });

        Assert.Null(ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Fact]
    public void WeightOnUnknownField_IsRejected()
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Text = new TextWeights { Weights = { ["Nope"] = 3 } },
        });

        Assert.Contains("Nope", ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// A weight on a field that contributes nothing to a text match could never do anything, so
    /// it is refused rather than accepted and ignored.
    /// </summary>
    [Fact]
    public void WeightOnNonSearchableField_IsRejected()
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Text = new TextWeights { Weights = { ["Rating"] = 3 } },
        });

        Assert.Contains("searchable", ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Theory]
    // Each function paired with a field of a type it cannot read.
    [InlineData("magnitude", "Name")]
    [InlineData("freshness", "Rating")]
    [InlineData("distance", "Rating")]
    [InlineData("tag", "Rating")]
    public void FunctionOverWrongFieldType_IsRejected(string type, string fieldName)
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Functions = [CreateFunction(type, fieldName)],
        });

        var error = ScoringProfileValidator.FindInvalidProfile(index);

        Assert.Contains(fieldName, error);
        Assert.Contains(type, error);
    }

    [Theory]
    [InlineData("magnitude")]
    [InlineData("freshness")]
    [InlineData("distance")]
    [InlineData("tag")]
    public void FunctionOverUnknownField_IsRejected(string type)
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Functions = [CreateFunction(type, "Nope")],
        });

        Assert.Contains("Nope", ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// Azure requires every scoring function's field to be filterable, and so does the
    /// emulator: the exact-value copies a function reads are only written for one.
    /// </summary>
    [Fact]
    public void FunctionOverNonFilterableField_IsRejected()
    {
        var index = WithProfile(new ScoringProfile
        {
            Name = "boost",
            Functions = [CreateFunction("magnitude", "Unfilterable")],
        });

        Assert.Contains("filterable", ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// A tag function's range is a proportion of tags rather than a field value, and Azure
    /// refuses the curves that would shape it.
    /// </summary>
    [Theory]
    [InlineData(ScoringFunctionInterpolation.Quadratic)]
    [InlineData(ScoringFunctionInterpolation.Logarithmic)]
    public void TagFunctionWithDisallowedInterpolation_IsRejected(ScoringFunctionInterpolation interpolation)
    {
        var function = (TagScoringFunction)CreateFunction("tag", "Tags");
        function.Interpolation = interpolation;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Contains("interpolation", ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Theory]
    [InlineData(ScoringFunctionInterpolation.Linear)]
    [InlineData(ScoringFunctionInterpolation.Constant)]
    public void TagFunctionWithAllowedInterpolation_IsAccepted(ScoringFunctionInterpolation interpolation)
    {
        var function = (TagScoringFunction)CreateFunction("tag", "Tags");
        function.Interpolation = interpolation;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Null(ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// Azure defines boost as "a positive number not equal to 1.0", and both bounds matter: a
    /// boost below 1 would invert the curve and demote the documents the function exists to
    /// promote, and an unbounded one overflows the float a score is returned as, leaving every
    /// document unorderable and the score not legal JSON.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(double.MaxValue / 2)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void FunctionWithOutOfRangeBoost_IsRejected(double boost)
    {
        var function = CreateFunction("magnitude", "Rating");
        function.Boost = boost;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Contains("boost", ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Fact]
    public void FunctionWithBoostJustAboveOne_IsAccepted()
    {
        var function = CreateFunction("magnitude", "Rating");
        function.Boost = 1.01;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Null(ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// A range of zero width leaves nothing to interpolate across.
    /// </summary>
    [Fact]
    public void MagnitudeWithEmptyRange_IsRejected()
    {
        var function = (MagnitudeScoringFunction)CreateFunction("magnitude", "Rating");
        function.Magnitude!.BoostingRangeEnd = function.Magnitude.BoostingRangeStart;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Contains("must differ", ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// A descending range is legal, and is how Azure documents boosting cheaper items.
    /// </summary>
    [Fact]
    public void MagnitudeWithReversedRange_IsAccepted()
    {
        var function = (MagnitudeScoringFunction)CreateFunction("magnitude", "Rating");
        function.Magnitude!.BoostingRangeStart = 5;
        function.Magnitude.BoostingRangeEnd = 0;

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Null(ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Fact]
    public void FreshnessWithMalformedDuration_IsRejected()
    {
        var function = (FreshnessScoringFunction)CreateFunction("freshness", "Updated");
        function.Freshness!.BoostingDuration = "365 days";

        var index = WithProfile(new ScoringProfile { Name = "boost", Functions = [function] });

        Assert.Contains("duration", ScoringProfileValidator.FindInvalidProfile(index));
    }

    [Fact]
    public void DefaultScoringProfileNamingNothing_IsRejected()
    {
        var index = LuceneTestHelper.CreateScoringIndex();
        index.DefaultScoringProfile = "nope";

        Assert.Contains("nope", ScoringProfileValidator.FindInvalidProfile(index));
    }

    /// <summary>
    /// Profile names are matched case-insensitively, so two differing only in case would make a
    /// request naming either one ambiguous.
    /// </summary>
    [Fact]
    public void DuplicateProfileNames_AreRejected()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        index.ScoringProfiles.Add(new ScoringProfile { Name = "boost" });
        index.ScoringProfiles.Add(new ScoringProfile { Name = "BOOST" });

        Assert.Contains("more than one", ScoringProfileValidator.FindInvalidProfile(index));
    }

    // ===== scoringParameter parsing =====

    /// <summary>
    /// The name is everything before the FIRST dash. Azure's own example has two dashes only
    /// because the longitude is negative; splitting on the pair would break any reference point
    /// east of Greenwich.
    /// </summary>
    [Fact]
    public void ReferencePoint_WithNegativeLongitude_IsParsed()
    {
        var parameters = ScoringParameterCollection.Parse(["mylocation--122.2,44.8"]);

        var point = parameters.GetReferencePoint("mylocation");

        Assert.NotNull(point);
        Assert.Equal(-122.2, point.Value.Lon, 5);
        Assert.Equal(44.8, point.Value.Lat, 5);
    }

    [Fact]
    public void ReferencePoint_WithPositiveLongitude_IsParsed()
    {
        var parameters = ScoringParameterCollection.Parse(["berlin-13.4,52.5"]);

        var point = parameters.GetReferencePoint("berlin");

        Assert.NotNull(point);
        Assert.Equal(13.4, point.Value.Lon, 5);
        Assert.Equal(52.5, point.Value.Lat, 5);
    }

    [Fact]
    public void Tags_AreParsedAsAList()
    {
        var parameters = ScoringParameterCollection.Parse(["mytags-luxury,budget"]);

        Assert.Equal(["luxury", "budget"], parameters.GetValues("mytags"));
    }

    [Fact]
    public void MissingParameter_ReadsAsNull()
    {
        Assert.Null(ScoringParameterCollection.Parse([]).GetValues("mytags"));
    }

    [Fact]
    public void ParameterWithoutASeparator_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScoringParameterCollection.Parse(["nodashhere"]));

        Assert.Contains("name-value", ex.Message);
    }

    [Fact]
    public void ReferencePointThatIsNotCoordinates_IsRejected()
    {
        var parameters = ScoringParameterCollection.Parse(["mylocation-north"]);

        Assert.Throws<InvalidOperationException>(() => parameters.GetReferencePoint("mylocation"));
    }

    /// <summary>
    /// A profile whose functions need parameters the request omitted is refused rather than run
    /// unboosted, so the divergence surfaces where it is introduced.
    /// </summary>
    [Fact]
    public void MissingRequiredParameters_AreReportedTogether()
    {
        var profile = new ScoringProfile
        {
            Name = "boost",
            Functions = [CreateFunction("distance", "Location"), CreateFunction("tag", "Tags")],
        };

        var message = ScoringProfileSupport.GetMissingParameterMessage(
            profile, ScoringParameterCollection.Empty);

        Assert.Contains("here", message);
        Assert.Contains("mytags", message);
    }

    /// <summary>
    /// Magnitude and freshness are defined entirely by the index, so a profile of only those
    /// needs nothing from the request.
    /// </summary>
    [Fact]
    public void ProfileNeedingNoParameters_IsSatisfied()
    {
        var profile = new ScoringProfile
        {
            Name = "boost",
            Functions = [CreateFunction("magnitude", "Rating"), CreateFunction("freshness", "Updated")],
        };

        Assert.Null(ScoringProfileSupport.GetMissingParameterMessage(
            profile, ScoringParameterCollection.Empty));
    }

    private static ScoringFunction CreateFunction(string type, string fieldName) => type switch
    {
        "magnitude" => new MagnitudeScoringFunction
        {
            FieldName = fieldName,
            Boost = 2,
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 0, BoostingRangeEnd = 5 },
        },
        "freshness" => new FreshnessScoringFunction
        {
            FieldName = fieldName,
            Boost = 2,
            Freshness = new FreshnessScoringParameters { BoostingDuration = "P365D" },
        },
        "distance" => new DistanceScoringFunction
        {
            FieldName = fieldName,
            Boost = 2,
            Distance = new DistanceScoringParameters
            {
                ReferencePointParameter = "here",
                BoostingDistance = 10,
            },
        },
        "tag" => new TagScoringFunction
        {
            FieldName = fieldName,
            Boost = 2,
            Tag = new TagScoringParameters { TagsParameter = "mytags" },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static SearchIndex WithProfile(ScoringProfile profile)
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        // A field that no scoring function may target, for the filterable rule.
        index.Fields.Add(new SearchField
        {
            Name = "Unfilterable",
            Type = "Edm.Double",
            Filterable = false,
        });

        index.ScoringProfiles.Add(profile);

        return index;
    }
}
