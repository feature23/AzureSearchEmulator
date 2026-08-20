using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// One entry of an index's <c>normalizers</c> array (issue #74).
/// </summary>
/// <remarks>
/// A normalizer is what a filter, facet or sort reads text through, in the place an analyzer
/// would sit for a full-text search. Azure defines exactly one shape for a custom one —
/// <c>#Microsoft.Azure.Search.CustomNormalizer</c> — so unlike
/// <see cref="LexicalAnalyzerDefinition"/> this needs no hierarchy and no converter to pick a
/// subtype.
///
/// It is an analyzer's chain minus the tokenizer: char filters rewrite the raw text and token
/// filters transform it, but nothing splits it, because a normalizer always produces exactly
/// one token. That is the property the whole feature rests on — a filter compares one whole
/// value against another, so a component that split the value would leave nothing to compare.
///
/// Carries its own <see cref="AdditionalProperties"/> for the reason given on
/// <see cref="LexicalAnalyzerDefinition"/>: <see cref="JsonExtensionDataAttribute"/> applies per
/// type rather than recursively, and without it every property Azure adds here later would be
/// dropped from a definition that round-trips through the emulator.
/// </remarks>
public class NormalizerDefinition
{
    /// <summary>
    /// Azure's discriminator for a custom normalizer.
    /// </summary>
    public const string CustomNormalizerType = "#Microsoft.Azure.Search.CustomNormalizer";

    /// <summary>
    /// The name a field's <c>normalizer</c> refers to.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The definition's Azure type.
    /// </summary>
    /// <remarks>
    /// Defaulted rather than required. Azure only defines the one value, so a definition that
    /// omits it is unambiguous, and rejecting it would refuse an index over a property that
    /// carries no information.
    /// </remarks>
    [JsonPropertyName(AnalysisComponentJson.TypeProperty)]
    public string ODataType { get; set; } = CustomNormalizerType;

    /// <summary>
    /// Filters applied to the token, in the order given.
    /// </summary>
    /// <remarks>
    /// Order is significant — folding accents before mapping characters is not the same chain
    /// as after — so this is a list rather than a set.
    /// </remarks>
    public IList<string> TokenFilters { get; set; } = new List<string>();

    /// <summary>
    /// Filters applied to the raw text before the token is formed, in the order given.
    /// </summary>
    public IList<string> CharFilters { get; set; } = new List<string>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
