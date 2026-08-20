using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// A synonym map: a named set of rules that widen a query's terms at search time (issue #69).
/// </summary>
/// <remarks>
/// Unlike an analyzer or a normalizer, this is not part of an index definition. It is a
/// service-level resource with its own <c>/synonymmaps</c> routes and its own lifetime, and a
/// field opts into it by naming it in <see cref="SearchField.SynonymMaps"/>. That indirection is
/// the point of the feature: one map can be edited once and take effect across every field of
/// every index that names it, without any of them being rewritten.
///
/// Azure applies the rules at query time only — never while indexing. Expanding at index time
/// would bake today's rules into the stored terms, so editing a map afterwards would leave the
/// documents indexed before the edit disagreeing with the ones after it, and the only repair
/// would be a full reindex. Expanding the query instead keeps the rules a property of the
/// search, which is why <see cref="SearchData.SynonymMapHelper"/> is reached from the search
/// analyzer and not from the indexing path.
/// </remarks>
public class SynonymMap
{
    /// <summary>
    /// The only format Azure supports, and the one the parser understands.
    /// </summary>
    public const string SolrFormat = "solr";

    /// <summary>
    /// The name a field's <c>synonymMaps</c> entry refers to.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The format the rules in <see cref="Synonyms"/> are written in.
    /// </summary>
    /// <remarks>
    /// Defaulted rather than required, for the reason <see cref="NormalizerDefinition.ODataType"/>
    /// is: Azure defines exactly one accepted value, so a definition that omits it is
    /// unambiguous. <see cref="Indexing.SynonymMapValidator"/> rejects any other value, because
    /// a map claiming a format the emulator cannot parse would otherwise be stored and then
    /// silently expand nothing.
    /// </remarks>
    public string Format { get; set; } = SolrFormat;

    /// <summary>
    /// The rules, in Solr syntax, separated by newlines.
    /// </summary>
    /// <remarks>
    /// Azure carries these as one newline-delimited string rather than an array, which is why
    /// this is a string here too — splitting it into a list would change the wire shape the
    /// Azure Search SDK writes and reads.
    ///
    /// Two forms are meaningful, and they do different things:
    /// <c>usa, united states</c> makes every term equivalent to every other, while
    /// <c>dog =&gt; canine, hound</c> replaces the left side with the right and drops the
    /// original term from the query.
    /// </remarks>
    public string Synonyms { get; set; } = "";

    /// <summary>
    /// The map's entity tag, echoed back so a client's optimistic concurrency check has
    /// something to compare against.
    /// </summary>
    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; set; }

    /// <remarks>
    /// Present for the reason given on <see cref="NormalizerDefinition.AdditionalProperties"/>:
    /// without it, properties the emulator does not model — <c>encryptionKey</c> among them —
    /// would be dropped from a map that is read back and written again.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
