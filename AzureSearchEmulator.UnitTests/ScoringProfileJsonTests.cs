using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the wire format of a scoring profile, which has to match Azure's exactly for
/// a definition to survive a round-trip through the Azure SDK (issue #47).
/// </summary>
public class ScoringProfileJsonTests
{
    /// <summary>
    /// The same options the app serializes with, including the camelCase policies whose effect
    /// on the weight keys is the subject of one of the tests below.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ProfileJson =
        """
        {
          "name": "hotels",
          "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
          "defaultScoringProfile": "boost",
          "scoringProfiles": [
            {
              "name": "boost",
              "text": { "weights": { "HotelName": 3.5, "Description": 1 } },
              "functionAggregation": "maximum",
              "functions": [
                {
                  "type": "magnitude",
                  "fieldName": "Rating",
                  "boost": 2.5,
                  "interpolation": "quadratic",
                  "magnitude": {
                    "boostingRangeStart": 0,
                    "boostingRangeEnd": 5,
                    "constantBoostBeyondRange": true
                  }
                },
                {
                  "type": "freshness",
                  "fieldName": "LastRenovated",
                  "boost": 2,
                  "interpolation": "logarithmic",
                  "freshness": { "boostingDuration": "P365D" }
                },
                {
                  "type": "distance",
                  "fieldName": "Location",
                  "boost": 3,
                  "interpolation": "linear",
                  "distance": { "referencePointParameter": "here", "boostingDistance": 10 }
                },
                {
                  "type": "tag",
                  "fieldName": "Tags",
                  "boost": 4,
                  "interpolation": "constant",
                  "tag": { "tagsParameter": "mytags" }
                }
              ]
            }
          ]
        }
        """;

    private static SearchIndex Deserialize()
        => JsonSerializer.Deserialize<SearchIndex>(ProfileJson, Options)!;

    private static JsonObject RoundTrip()
        => JsonNode.Parse(JsonSerializer.Serialize(Deserialize(), Options))!.AsObject();

    [Fact]
    public void EveryFunctionType_DeserializesToItsOwnClass()
    {
        var functions = Deserialize().ScoringProfiles[0].Functions;

        Assert.Collection(
            functions,
            i => Assert.IsType<MagnitudeScoringFunction>(i),
            i => Assert.IsType<FreshnessScoringFunction>(i),
            i => Assert.IsType<DistanceScoringFunction>(i),
            i => Assert.IsType<TagScoringFunction>(i));
    }

    [Fact]
    public void FunctionParameters_Deserialize()
    {
        var functions = Deserialize().ScoringProfiles[0].Functions;

        var magnitude = Assert.IsType<MagnitudeScoringFunction>(functions[0]);
        Assert.Equal(5, magnitude.Magnitude!.BoostingRangeEnd);
        Assert.True(magnitude.Magnitude.ConstantBoostBeyondRange);
        Assert.Equal(ScoringFunctionInterpolation.Quadratic, magnitude.Interpolation);

        var freshness = Assert.IsType<FreshnessScoringFunction>(functions[1]);
        Assert.Equal("P365D", freshness.Freshness!.BoostingDuration);

        var distance = Assert.IsType<DistanceScoringFunction>(functions[2]);
        Assert.Equal("here", distance.Distance!.ReferencePointParameter);
        Assert.Equal(10, distance.Distance.BoostingDistance);

        var tag = Assert.IsType<TagScoringFunction>(functions[3]);
        Assert.Equal("mytags", tag.Tag!.TagsParameter);
    }

    [Fact]
    public void Aggregation_Deserializes()
    {
        Assert.Equal(
            ScoringFunctionAggregation.Maximum,
            Deserialize().ScoringProfiles[0].FunctionAggregation);
    }

    /// <summary>
    /// Omitted aggregation means <c>sum</c>, which is what Azure defaults to.
    /// </summary>
    [Fact]
    public void OmittedAggregation_DefaultsToSum()
    {
        const string json = """{ "name": "boost" }""";

        var profile = JsonSerializer.Deserialize<ScoringProfile>(json, Options)!;

        Assert.Equal(ScoringFunctionAggregation.Sum, profile.FunctionAggregation);
    }

    /// <summary>
    /// The keys of <c>text.weights</c> are field names, and Lucene field names are
    /// case-sensitive, so the global camelCase dictionary-key policy must not touch them: a
    /// <c>HotelName</c> rewritten to <c>hotelName</c> would match no field and silently stop
    /// boosting.
    /// </summary>
    [Fact]
    public void WeightKeys_KeepTheirCasing()
    {
        var weights = RoundTrip()["scoringProfiles"]?[0]?["text"]?["weights"]?.AsObject();

        Assert.NotNull(weights);
        Assert.True(weights.ContainsKey("HotelName"), "the weight key was rewritten to camelCase");
        Assert.Equal(3.5, weights["HotelName"]?.GetValue<double>());
    }

    /// <summary>
    /// Azure discriminates the function subtypes with a plain <c>type</c> property, not the
    /// <c>@odata.type</c> it uses elsewhere, and writes it exactly once.
    /// </summary>
    [Fact]
    public void FunctionType_IsWrittenOnceAsPlainType()
    {
        var function = RoundTrip()["scoringProfiles"]?[0]?["functions"]?[0]?.AsObject();

        Assert.NotNull(function);
        Assert.Equal("magnitude", function["type"]?.GetValue<string>());
        Assert.False(function.ContainsKey("@odata.type"));
    }

    /// <summary>
    /// The service reads and writes <c>constantBoostBeyondRange</c>; the Azure SDK's own
    /// property name for it never appears on the wire.
    /// </summary>
    [Fact]
    public void ConstantBoostBeyondRange_UsesTheWireName()
    {
        var magnitude = RoundTrip()["scoringProfiles"]?[0]?["functions"]?[0]?["magnitude"]?.AsObject();

        Assert.NotNull(magnitude);
        Assert.True(magnitude.ContainsKey("constantBoostBeyondRange"));
        Assert.False(magnitude.ContainsKey("shouldBoostBeyondRangeByConstant"));
    }

    [Fact]
    public void EnumsAreWrittenInCamelCase()
    {
        var profile = RoundTrip()["scoringProfiles"]?[0];

        Assert.Equal("maximum", profile?["functionAggregation"]?.GetValue<string>());
        Assert.Equal("quadratic", profile?["functions"]?[0]?["interpolation"]?.GetValue<string>());
        Assert.Equal("constant", profile?["functions"]?[3]?["interpolation"]?.GetValue<string>());
    }

    [Fact]
    public void DefaultScoringProfile_RoundTrips()
    {
        Assert.Equal("boost", RoundTrip()["defaultScoringProfile"]?.GetValue<string>());
    }

    /// <summary>
    /// An unrecognized value is reported rather than falling back to the enum's default, which
    /// would turn a typo into silently different ranking.
    /// </summary>
    [Fact]
    public void UnknownInterpolation_IsRejected()
    {
        const string json =
            """
            {
              "name": "boost",
              "functions": [
                { "type": "magnitude", "fieldName": "Rating", "interpolation": "lienar",
                  "magnitude": { "boostingRangeStart": 0, "boostingRangeEnd": 5 } }
              ]
            }
            """;

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ScoringProfile>(json, Options));

        Assert.Contains("lienar", ex.Message);
    }

    [Fact]
    public void UnknownFunctionType_IsRejected()
    {
        const string json =
            """
            { "name": "boost", "functions": [ { "type": "gravity", "fieldName": "Rating" } ] }
            """;

        var ex = Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ScoringProfile>(json, Options));

        Assert.Contains("gravity", ex.Message);
    }

    [Fact]
    public void FunctionWithoutAType_IsRejected()
    {
        const string json = """{ "name": "boost", "functions": [ { "fieldName": "Rating" } ] }""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ScoringProfile>(json, Options));
    }

    /// <summary>
    /// Durations round-trip through the XSD form Azure emits rather than .NET's own TimeSpan
    /// formatting, which the service does not accept.
    /// </summary>
    [Theory]
    [InlineData("P365D")]
    [InlineData("PT1H")]
    [InlineData("P2DT12H")]
    [InlineData("-P30D")]
    public void Durations_RoundTripInXsdForm(string duration)
    {
        var parsed = ScoringProfileJson.ParseDuration(duration);

        Assert.Equal(duration, ScoringProfileJson.FormatDuration(parsed));
    }

    [Fact]
    public void MalformedDuration_IsRejected()
    {
        Assert.Throws<FormatException>(() => ScoringProfileJson.ParseDuration("365 days"));
    }
}
