using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace AzureSearchEmulator.Models;

/// <summary>
/// The wire vocabulary of a scoring profile: the discriminator values, the enum spellings, and
/// the duration format (issue #47).
/// </summary>
/// <remarks>
/// Azure spells all of these in camelCase, and the emulator has to read and write exactly the
/// same strings for a definition to round-trip through the Azure SDK unchanged. They are
/// gathered here so the converters, the validator and the tests all agree on one spelling.
/// </remarks>
public static class ScoringProfileJson
{
    public const string MagnitudeType = "magnitude";
    public const string FreshnessType = "freshness";
    public const string DistanceType = "distance";
    public const string TagType = "tag";

    public const string TypeProperty = "type";

    /// <summary>
    /// Every discriminator the service accepts, for error messages that can name the valid set.
    /// </summary>
    public static readonly IReadOnlyList<string> FunctionTypes =
        [MagnitudeType, FreshnessType, DistanceType, TagType];

    /// <summary>
    /// Parses an XSD duration such as <c>P365D</c> or <c>-PT1H</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="XmlConvert.ToTimeSpan(string)"/> implements the XSD grammar Azure documents,
    /// including the negative durations that reverse a freshness function's direction. Its
    /// exception type is not part of its contract, so any failure is normalized into the
    /// <see cref="FormatException"/> callers can act on.
    /// </remarks>
    public static TimeSpan ParseDuration(string value)
    {
        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new FormatException(
                $"'{value}' is not a valid XSD duration; expected a value such as 'P365D' or 'PT1H'.");
        }
    }

    /// <summary>
    /// Renders a duration back into the XSD form Azure emits.
    /// </summary>
    public static string FormatDuration(TimeSpan value) => XmlConvert.ToString(value);
}

/// <summary>
/// Serializes a dictionary without applying the global camelCase key policy.
/// </summary>
/// <remarks>
/// The keys of <see cref="TextWeights.Weights"/> are field names chosen by the caller, not
/// property names, so the naming policy that is correct for the rest of the document is wrong
/// for them: it would rewrite <c>HotelName</c> to <c>hotelName</c>, and since Lucene field
/// names are case-sensitive the weight would then apply to no field at all. Worse, the failure
/// is silent — the profile round-trips looking plausible while doing nothing.
///
/// Deserialization needs no special handling (the policy only ever applies on write), but it is
/// implemented here so the type has one converter governing both directions.
/// </remarks>
public class VerbatimDictionaryKeyConverter : JsonConverter<Dictionary<string, double>>
{
    public override Dictionary<string, double> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object of field name to weight.");
        }

        var result = new Dictionary<string, double>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return result;
            }

            var name = reader.GetString()!;

            reader.Read();

            result[name] = reader.GetDouble();
        }

        throw new JsonException("Unexpected end of the weights object.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, double> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (var (name, weight) in value)
        {
            // WritePropertyName rather than the options' key policy, which is the whole point
            // of this converter.
            writer.WritePropertyName(name);
            writer.WriteNumberValue(weight);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Reads and writes the <see cref="ScoringFunction"/> hierarchy, which Azure discriminates with
/// a plain <c>"type"</c> property.
/// </summary>
/// <remarks>
/// Written by hand rather than with <see cref="JsonDerivedTypeAttribute"/> because that
/// mechanism owns the discriminator property: it insists on writing the value itself and
/// requires it to be absent from the subtypes, which makes the C# model awkward for a property
/// that is genuinely part of each function's identity. Reading the discriminator here also lets
/// an unrecognized function type be reported by name, as a definition error the caller can act
/// on, rather than surfacing as a bare deserialization failure.
/// </remarks>
public class ScoringFunctionConverter : JsonConverter<ScoringFunction>
{
    public override ScoringFunction Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        if (!element.TryGetProperty(ScoringProfileJson.TypeProperty, out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(
                "A scoring function must have a 'type' of " +
                $"{string.Join(", ", ScoringProfileJson.FunctionTypes)}.");
        }

        var typeName = type.GetString();

        // The subtypes are deserialized with the caller's options minus this converter, which
        // would otherwise recurse into itself on the very object it is building.
        var inner = WithoutSelf(options);

        var json = element.GetRawText();

        return typeName switch
        {
            ScoringProfileJson.MagnitudeType =>
                JsonSerializer.Deserialize<MagnitudeScoringFunction>(json, inner)!,
            ScoringProfileJson.FreshnessType =>
                JsonSerializer.Deserialize<FreshnessScoringFunction>(json, inner)!,
            ScoringProfileJson.DistanceType =>
                JsonSerializer.Deserialize<DistanceScoringFunction>(json, inner)!,
            ScoringProfileJson.TagType =>
                JsonSerializer.Deserialize<TagScoringFunction>(json, inner)!,
            _ => throw new JsonException(
                $"'{typeName}' is not a supported scoring function type; expected one of " +
                $"{string.Join(", ", ScoringProfileJson.FunctionTypes)}.")
        };
    }

    public override void Write(Utf8JsonWriter writer, ScoringFunction value, JsonSerializerOptions options)
    {
        var inner = WithoutSelf(options);

        // Serialized against the concrete type so the subtype's own parameters are written;
        // declaring the property as the base type would otherwise emit only the shared ones.
        using var document = JsonSerializer.SerializeToDocument(value, value.GetType(), inner);

        writer.WriteStartObject();

        // The discriminator is written here rather than by the model, so that it leads the
        // object as Azure writes it.
        writer.WriteString(ScoringProfileJson.TypeProperty, value.Type);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            // [JsonIgnore] on the abstract Type property is not inherited by the concrete
            // overrides, so whether the subtype serializes a "type" of its own depends on how
            // the serializer was configured. Skipping it here makes the discriminator appear
            // exactly once either way — writing it twice produces JSON that parses as a
            // duplicate key and throws on the way back in.
            if (!property.NameEquals(ScoringProfileJson.TypeProperty))
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Copies the options with this converter removed, so serializing a subtype does not
    /// re-enter the converter that is already handling it.
    /// </summary>
    private static JsonSerializerOptions WithoutSelf(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);

        for (var i = copy.Converters.Count - 1; i >= 0; i--)
        {
            if (copy.Converters[i] is ScoringFunctionConverter)
            {
                copy.Converters.RemoveAt(i);
            }
        }

        return copy;
    }
}

/// <summary>
/// Writes the scoring enums in the camelCase Azure uses, and reads them case-insensitively.
/// </summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> with a camelCase policy would cover
/// <see cref="ScoringFunctionInterpolation"/>, but it cannot be applied globally here: the
/// emulator's other enums are not all camelCase on the wire, and attaching it per-property
/// still leaves the failure mode where an unrecognized value deserializes as the default rather
/// than being reported. Naming the invalid value keeps a typo like <c>"lienar"</c> from
/// silently becoming <c>linear</c>.
/// </remarks>
public class CamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();

        if (value != null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new JsonException(
            $"'{value}' is not a valid {typeof(TEnum).Name}; expected one of " +
            $"{string.Join(", ", Enum.GetNames<TEnum>().Select(ToCamelCase))}.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToCamelCase(value.ToString()));

    private static string ToCamelCase(string name)
        => string.IsNullOrEmpty(name)
            ? name
            : char.ToLower(name[0], CultureInfo.InvariantCulture) + name[1..];
}
