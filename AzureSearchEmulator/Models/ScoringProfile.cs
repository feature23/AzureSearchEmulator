using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// A named relevance-tuning profile on an index (issue #47).
/// </summary>
/// <remarks>
/// A profile adjusts the score a document would otherwise get, in two independent ways:
/// <see cref="Text"/> weights multiply the contribution of individual searchable fields to the
/// text match itself, while <see cref="Functions"/> boost documents by the value of a field —
/// how large a number is, how recent a date is, how near a point is, or whether a tag matches.
///
/// The two are deliberately separate: weights change how the query text is matched, so they
/// have to be applied while the query is parsed, whereas functions do not depend on the query
/// text at all and are applied to the score of whatever matched.
/// </remarks>
public class ScoringProfile
{
    /// <summary>
    /// The name callers pass as <c>scoringProfile</c>, and that
    /// <see cref="SearchIndex.DefaultScoringProfile"/> refers to.
    /// </summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>
    /// Per-field weights applied to the text match.
    /// </summary>
    [JsonPropertyName("text")]
    public TextWeights? Text { get; set; }

    /// <summary>
    /// Functions that boost a document by the value of one of its fields.
    /// </summary>
    public IList<ScoringFunction> Functions { get; set; } = new List<ScoringFunction>();

    /// <summary>
    /// How the boosts of the individual <see cref="Functions"/> are combined into one.
    /// </summary>
    /// <remarks>
    /// Azure defaults this to <c>sum</c> when it is omitted.
    /// </remarks>
    [JsonConverter(typeof(CamelCaseEnumConverter<ScoringFunctionAggregation>))]
    public ScoringFunctionAggregation FunctionAggregation { get; set; } = ScoringFunctionAggregation.Sum;
}

/// <summary>
/// The <c>text.weights</c> of a <see cref="ScoringProfile"/>, mapping a searchable field to the
/// weight its match contributes.
/// </summary>
/// <remarks>
/// This exists as a wrapper class rather than the dictionary alone because Azure nests the map
/// one level deep, as <c>"text": { "weights": { ... } }</c>.
/// </remarks>
public class TextWeights
{
    /// <summary>
    /// Field name to weight. A field absent from the map keeps its default weight of 1.
    /// </summary>
    /// <remarks>
    /// The keys are field names, so they must survive serialization exactly as written.
    /// <c>DictionaryKeyPolicy</c> is set to camelCase globally (see <c>Program.cs</c>), which
    /// would rewrite a key like <c>HotelName</c> to <c>hotelName</c> and leave it matching no
    /// field at all — the weight would then be silently ignored rather than applied.
    /// <see cref="VerbatimDictionaryKeyConverter"/> pins the keys against that policy.
    /// </remarks>
    [JsonConverter(typeof(VerbatimDictionaryKeyConverter))]
    public Dictionary<string, double> Weights { get; set; } = new();
}

/// <summary>
/// Defines how a function's boost is interpolated across its range.
/// </summary>
/// <remarks>
/// Serialized in camelCase to match Azure's wire format; see
/// <see cref="ScoringProfileJson.Interpolation"/> for the conversion.
/// </remarks>
public enum ScoringFunctionInterpolation
{
    Linear,
    Constant,
    Quadratic,
    Logarithmic,
}

/// <summary>
/// Defines how the boosts of several scoring functions combine.
/// </summary>
public enum ScoringFunctionAggregation
{
    Sum,
    Average,
    Minimum,
    Maximum,
    FirstMatching,
    Product,
}

/// <summary>
/// Base type for the four scoring functions, carrying the properties they share.
/// </summary>
/// <remarks>
/// Azure discriminates the subtypes with a plain <c>"type"</c> property rather than the
/// <c>@odata.type</c> discriminator it uses elsewhere, which is why this hierarchy is
/// deserialized by <see cref="ScoringFunctionConverter"/> rather than by
/// <see cref="JsonDerivedTypeAttribute"/>: the built-in polymorphism writes the discriminator
/// itself and would need the property removed from the subtypes to avoid emitting it twice.
/// </remarks>
[JsonConverter(typeof(ScoringFunctionConverter))]
public abstract class ScoringFunction
{
    /// <summary>
    /// The discriminator value, i.e. <c>magnitude</c>, <c>freshness</c>, <c>distance</c> or
    /// <c>tag</c>.
    /// </summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>
    /// The field whose value the function reads.
    /// </summary>
    [Required]
    public string FieldName { get; set; } = "";

    /// <summary>
    /// The multiplier applied to a document at the strongest end of the function's range.
    /// </summary>
    public double Boost { get; set; } = 1;

    /// <summary>
    /// How the boost falls off across the range; linear when omitted.
    /// </summary>
    [JsonConverter(typeof(CamelCaseEnumConverter<ScoringFunctionInterpolation>))]
    public ScoringFunctionInterpolation Interpolation { get; set; } = ScoringFunctionInterpolation.Linear;
}

/// <summary>
/// Boosts a document by how large a numeric field's value is.
/// </summary>
public class MagnitudeScoringFunction : ScoringFunction
{
    public override string Type => ScoringProfileJson.MagnitudeType;

    [Required]
    public MagnitudeScoringParameters? Magnitude { get; set; }
}

public class MagnitudeScoringParameters
{
    public double BoostingRangeStart { get; set; }

    public double BoostingRangeEnd { get; set; }

    /// <summary>
    /// Whether documents past the far end of the range keep the full boost rather than none.
    /// </summary>
    /// <remarks>
    /// The JSON name is <c>constantBoostBeyondRange</c>, which is what the service reads and
    /// writes; the Azure SDK's own property is named <c>ShouldBoostBeyondRangeByConstant</c>,
    /// but that name never appears on the wire.
    /// </remarks>
    [JsonPropertyName("constantBoostBeyondRange")]
    public bool ConstantBoostBeyondRange { get; set; }
}

/// <summary>
/// Boosts a document by how recent a <c>Edm.DateTimeOffset</c> field's value is.
/// </summary>
public class FreshnessScoringFunction : ScoringFunction
{
    public override string Type => ScoringProfileJson.FreshnessType;

    [Required]
    public FreshnessScoringParameters? Freshness { get; set; }
}

public class FreshnessScoringParameters
{
    /// <summary>
    /// The age at which the boost has fully decayed, as an XSD duration such as <c>P365D</c>.
    /// </summary>
    /// <remarks>
    /// Serialized as a string rather than a <see cref="TimeSpan"/> because System.Text.Json
    /// writes a <see cref="TimeSpan"/> in .NET's own <c>d.hh:mm:ss</c> form, which Azure does
    /// not accept. <see cref="ScoringProfileJson.ParseDuration"/> does the conversion.
    /// </remarks>
    public string? BoostingDuration { get; set; }
}

/// <summary>
/// Boosts a document by how near a geography point is to a reference point supplied per query.
/// </summary>
public class DistanceScoringFunction : ScoringFunction
{
    public override string Type => ScoringProfileJson.DistanceType;

    [Required]
    public DistanceScoringParameters? Distance { get; set; }
}

public class DistanceScoringParameters
{
    /// <summary>
    /// Names the <c>scoringParameter</c> carrying the reference point for this query.
    /// </summary>
    /// <remarks>
    /// The point is not part of the index definition because it is usually the user's own
    /// location, which differs per query.
    /// </remarks>
    [Required]
    public string ReferencePointParameter { get; set; } = "";

    /// <summary>
    /// The distance in kilometers at which the boost has fully decayed.
    /// </summary>
    public double BoostingDistance { get; set; }
}

/// <summary>
/// Boosts a document whose tag field shares values with a list supplied per query.
/// </summary>
public class TagScoringFunction : ScoringFunction
{
    public override string Type => ScoringProfileJson.TagType;

    [Required]
    public TagScoringParameters? Tag { get; set; }
}

public class TagScoringParameters
{
    /// <summary>
    /// Names the <c>scoringParameter</c> carrying the caller's tags for this query.
    /// </summary>
    [Required]
    public string TagsParameter { get; set; } = "";
}
