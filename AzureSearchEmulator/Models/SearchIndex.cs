using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

public class SearchIndex
{
    public string Name { get; init; } = "";

    public IList<SearchField> Fields { get; init; } = new List<SearchField>();

    /// <summary>
    /// Suggesters available for <c>docs/suggest</c> and <c>docs/autocomplete</c> (issue #45).
    /// </summary>
    public IList<SearchSuggester> Suggesters { get; init; } = new List<SearchSuggester>();

    /// <summary>
    /// Relevance-tuning profiles available to <c>search</c> (issue #47).
    /// </summary>
    public IList<ScoringProfile> ScoringProfiles { get; init; } = new List<ScoringProfile>();

    /// <summary>
    /// The profile applied when a request names none.
    /// </summary>
    /// <remarks>
    /// Null means unscored relevance, which is the default. A request's own
    /// <c>scoringProfile</c> overrides this.
    /// </remarks>
    public string? DefaultScoringProfile { get; set; }

    /// <summary>
    /// Vector search algorithms and profiles available to <c>Collection(Edm.Single)</c> fields
    /// (issue #46).
    /// </summary>
    /// <remarks>
    /// Null when the index defines no vector configuration, which is both the Azure default and
    /// the state of every index the emulator wrote before this was modelled. Kept nullable
    /// rather than defaulted to an empty instance so that an index without vector search does
    /// not grow an empty <c>vectorSearch</c> object on round-trip.
    ///
    /// Omitted when null rather than written as <c>"vectorSearch": null</c>, so that an index
    /// with no vector configuration keeps the definition it was created with.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VectorSearch? VectorSearch { get; set; }

    /// <summary>
    /// Index properties the emulator does not model, kept verbatim so they survive a
    /// get-modify-put cycle (issue #41).
    /// </summary>
    /// <remarks>
    /// A client that reads an index, changes one field and writes it back would otherwise
    /// have <c>analyzers</c>, <c>corsOptions</c>, <c>similarity</c>,
    /// <c>semantic</c> and <c>encryptionKey</c> silently deleted from
    /// its own definition — the emulator would be destroying configuration rather than
    /// merely ignoring it. Capturing them here costs nothing and keeps the stored definition
    /// faithful, even though the features behind them stay unimplemented.
    ///
    /// <see cref="JsonExtensionDataAttribute"/> is also what makes an unrecognized property a
    /// non-error: without it, tightening deserialization to reject unmapped members (needed so
    /// typos surface) would reject every one of these too.
    ///
    /// The dictionary type is not interchangeable: a <c>JsonObject</c> is accepted here but
    /// serializes the captured properties nested inside a stray unnamed object, corrupting
    /// the JSON. <see cref="JsonElement"/> values round-trip verbatim, preserving nested
    /// structure, numeric types and the <c>@odata.type</c> discriminators that give a
    /// polymorphic analyzer or similarity definition its meaning.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }

    /// <summary>
    /// Finds the suggester a request named, matching case-insensitively as Azure Search does,
    /// or null when the index defines no suggester by that name.
    /// </summary>
    public SearchSuggester? FindSuggester(string name)
        => Suggesters.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the scoring profile a request named, or null when the index defines none by that
    /// name (issue #47).
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively, for the same reason <see cref="FindSuggester"/> is.
    /// </remarks>
    public ScoringProfile? FindScoringProfile(string name)
        => ScoringProfiles.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public SearchField GetKeyField()
    {
        var keys = Fields.Where(i => i.Key.GetValueOrDefault()).ToList();

        return keys.Count switch
        {
            0 => throw new InvalidOperationException("Index does not have a configured key"),
            > 1 => throw new InvalidOperationException("Index has more than one configured key"),
            _ => keys[0]
        };
    }
}
