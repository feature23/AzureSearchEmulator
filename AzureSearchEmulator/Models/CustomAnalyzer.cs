using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// The wire vocabulary of a custom analysis chain: the <c>@odata.type</c> discriminators and
/// the property that carries them (issue #34).
/// </summary>
/// <remarks>
/// Azure discriminates the four analysis component families on <c>@odata.type</c> rather than
/// the bare <c>type</c> that a scoring function uses, and spells every value with the
/// <c>#Microsoft.Azure.Search.</c> prefix. Gathered here so the converters, the builder and the
/// validator all agree on one spelling.
///
/// A component with no options carries no <c>@odata.type</c> at all — Azure documents that the
/// discriminator "is only provided for tokenizers that can be customized". That is why the
/// converters below treat a missing discriminator as legal rather than as a malformed
/// definition.
/// </remarks>
public static class AnalysisComponentJson
{
    public const string TypeProperty = "@odata.type";

    private const string Prefix = "#Microsoft.Azure.Search.";

    public const string CustomAnalyzerType = Prefix + "CustomAnalyzer";
    public const string PatternAnalyzerType = Prefix + "PatternAnalyzer";
    public const string StandardAnalyzerType = Prefix + "StandardAnalyzer";
    public const string StopAnalyzerType = Prefix + "StopAnalyzer";

    /// <summary>
    /// Every analyzer discriminator the emulator understands, for error messages that can name
    /// the valid set.
    /// </summary>
    public static readonly IReadOnlyList<string> AnalyzerTypes =
        [CustomAnalyzerType, PatternAnalyzerType, StandardAnalyzerType, StopAnalyzerType];

    /// <summary>
    /// Azure's ceiling on a <c>maxTokenLength</c>.
    /// </summary>
    public const int MaxTokenLengthLimit = 300;

    /// <summary>
    /// Azure's default <c>maxTokenLength</c> for the standard analyzer.
    /// </summary>
    public const int DefaultMaxTokenLength = 255;
}

/// <summary>
/// One entry of an index's <c>analyzers</c> array.
/// </summary>
/// <remarks>
/// Azure's four analyzer shapes divide into two groups. <see cref="CustomAnalyzer"/> composes a
/// chain out of separately-defined components; the other three are built-in Lucene analyzers
/// with their options spelled inline. Both are named, and a field's <c>analyzer</c> refers to
/// either kind by name, so they share this base type.
///
/// Every subtype carries its own <see cref="AdditionalProperties"/>, for the reason given on
/// <see cref="VectorSearch"/>: modelling a property that previously rode through
/// <see cref="SearchIndex.AdditionalProperties"/> means anything not declared here starts being
/// dropped instead, and <see cref="JsonExtensionDataAttribute"/> applies per type rather than
/// recursively.
/// </remarks>
[JsonConverter(typeof(LexicalAnalyzerConverter))]
public abstract class LexicalAnalyzerDefinition
{
    /// <summary>
    /// The name a field's <c>analyzer</c>, <c>indexAnalyzer</c> or <c>searchAnalyzer</c> refers to.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The discriminator this definition is written back out with.
    /// </summary>
    [JsonIgnore]
    public abstract string ODataType { get; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// An analyzer assembled from a tokenizer, optional token filters and optional char filters.
/// </summary>
public class CustomAnalyzer : LexicalAnalyzerDefinition
{
    public override string ODataType => AnalysisComponentJson.CustomAnalyzerType;

    /// <summary>
    /// The tokenizer the chain is built on. Required, and exactly one.
    /// </summary>
    public string Tokenizer { get; set; } = "";

    /// <summary>
    /// Filters applied to the token stream, in the order given.
    /// </summary>
    /// <remarks>
    /// Order is significant — lowercasing before a stemmer is not the same chain as after it —
    /// so this is a list rather than a set.
    /// </remarks>
    public IList<string> TokenFilters { get; set; } = new List<string>();

    /// <summary>
    /// Filters applied to the raw text before tokenization, in the order given.
    /// </summary>
    public IList<string> CharFilters { get; set; } = new List<string>();
}

/// <summary>
/// Lucene's pattern analyzer, which tokenizes by splitting on a regular expression.
/// </summary>
public class PatternAnalyzerDefinition : LexicalAnalyzerDefinition
{
    public override string ODataType => AnalysisComponentJson.PatternAnalyzerType;

    /// <summary>
    /// The expression separating tokens. Azure's default splits on runs of non-word characters.
    /// </summary>
    public string Pattern { get; set; } = @"\W+";

    public bool LowerCase { get; set; } = true;

    public IList<string> Stopwords { get; set; } = new List<string>();
}

/// <summary>
/// Lucene's standard analyzer with its options spelled out.
/// </summary>
public class StandardAnalyzerDefinition : LexicalAnalyzerDefinition
{
    public override string ODataType => AnalysisComponentJson.StandardAnalyzerType;

    public int MaxTokenLength { get; set; } = AnalysisComponentJson.DefaultMaxTokenLength;

    public IList<string> Stopwords { get; set; } = new List<string>();
}

/// <summary>
/// Lucene's stop analyzer: letter tokenization, lowercased, with stopwords removed.
/// </summary>
public class StopAnalyzerDefinition : LexicalAnalyzerDefinition
{
    public override string ODataType => AnalysisComponentJson.StopAnalyzerType;

    public IList<string> Stopwords { get; set; } = new List<string>();
}

/// <summary>
/// One entry of an index's <c>tokenizers</c>, <c>tokenFilters</c> or <c>charFilters</c> array.
/// </summary>
/// <remarks>
/// These three families are modelled with one open type rather than a hierarchy per
/// <c>@odata.type</c>. Azure defines around 50 component types between them, each with its own
/// options, and the emulator does not interpret those options itself: it hands them to the
/// Lucene factory named by the component, which parses its own arguments from a string map.
/// Declaring 50 classes whose only job is to be flattened back into that map would add a great
/// deal of code and one more place for a spelling to drift, without making a single definition
/// work that would not work anyway.
///
/// So the options stay in <see cref="AdditionalProperties"/> and are converted to factory
/// arguments by <see cref="SearchData.CustomAnalyzerBuilder"/>. What this type does model is
/// the part every component shares and the builder needs: its <see cref="Name"/> and its
/// <see cref="ODataType"/>.
/// </remarks>
public class AnalysisComponentDefinition
{
    /// <summary>
    /// The name a <see cref="CustomAnalyzer"/> refers to this component by.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The component's Azure type, or null when it has no options and so carries no
    /// discriminator.
    /// </summary>
    [JsonPropertyName(AnalysisComponentJson.TypeProperty)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ODataType { get; set; }

    /// <summary>
    /// The component's options, held verbatim.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Reads and writes an analyzer definition against its <c>@odata.type</c>.
/// </summary>
/// <remarks>
/// Follows <see cref="ScoringFunctionConverter"/>, including the removal of itself from the
/// options it recurses with. It differs in one way: an unrecognized discriminator is not a
/// <see cref="JsonException"/> here.
///
/// Azure has analyzer types the emulator does not implement, and a definition carrying one is
/// still a definition a client may legitimately hold. Throwing would fail the whole request at
/// deserialization, before any validator could produce Azure's error envelope, and would make
/// the emulator refuse an index it could otherwise serve. Instead an unknown type is read as a
/// <see cref="CustomAnalyzer"/> — the shape whose properties the others are a subset of — and
/// keeps its real discriminator through <see cref="LexicalAnalyzerDefinition.AdditionalProperties"/>,
/// so it round-trips intact. <see cref="Indexing.AnalyzerValidator"/> is what decides whether
/// the emulator can actually build it.
/// </remarks>
public class LexicalAnalyzerConverter : JsonConverter<LexicalAnalyzerDefinition>
{
    public override LexicalAnalyzerDefinition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;

        var typeName = element.TryGetProperty(AnalysisComponentJson.TypeProperty, out var type)
                       && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

        var inner = WithoutSelf(options);
        var json = element.GetRawText();

        return typeName switch
        {
            AnalysisComponentJson.PatternAnalyzerType =>
                JsonSerializer.Deserialize<PatternAnalyzerDefinition>(json, inner)!,
            AnalysisComponentJson.StandardAnalyzerType =>
                JsonSerializer.Deserialize<StandardAnalyzerDefinition>(json, inner)!,
            AnalysisComponentJson.StopAnalyzerType =>
                JsonSerializer.Deserialize<StopAnalyzerDefinition>(json, inner)!,
            _ => JsonSerializer.Deserialize<CustomAnalyzer>(json, inner)!
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        LexicalAnalyzerDefinition value,
        JsonSerializerOptions options)
    {
        var inner = WithoutSelf(options);

        // Serialized against the concrete type so the subtype's own options are written.
        using var document = JsonSerializer.SerializeToDocument(value, value.GetType(), inner);

        writer.WriteStartObject();

        // An analyzer read from an unrecognized discriminator kept it in the extension bag, and
        // that spelling is the one the client sent: writing this type's own would silently
        // rewrite the definition into something it is not.
        var written = value.AdditionalProperties?.ContainsKey(AnalysisComponentJson.TypeProperty) == true;

        if (!written)
        {
            writer.WriteString(AnalysisComponentJson.TypeProperty, value.ODataType);
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static JsonSerializerOptions WithoutSelf(JsonSerializerOptions options)
    {
        var copy = new JsonSerializerOptions(options);

        for (var i = copy.Converters.Count - 1; i >= 0; i--)
        {
            if (copy.Converters[i] is LexicalAnalyzerConverter)
            {
                copy.Converters.RemoveAt(i);
            }
        }

        return copy;
    }
}
