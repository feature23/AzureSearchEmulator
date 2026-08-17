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
    /// Index properties the emulator does not model, kept verbatim so they survive a
    /// get-modify-put cycle (issue #41).
    /// </summary>
    /// <remarks>
    /// A client that reads an index, changes one field and writes it back would otherwise
    /// have <c>scoringProfiles</c>, <c>analyzers</c>, <c>corsOptions</c>, <c>similarity</c>,
    /// <c>semantic</c>, <c>vectorSearch</c> and <c>encryptionKey</c> silently deleted from
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
