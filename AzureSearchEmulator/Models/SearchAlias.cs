using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// An index alias: a service-level name that document operations resolve to one index.
/// </summary>
/// <remarks>
/// An alias lets a client address a stable name while the index behind it is swapped, for
/// example when a new index is built alongside the old one and traffic is cut over. Azure
/// models the target as an array but accepts exactly one entry, so <see cref="Indexes"/> is a
/// list on the wire and <see cref="Indexing.AliasValidator"/> enforces the single entry.
///
/// Only document routes (search, get, count, suggest, autocomplete, index) resolve an alias.
/// The <c>/indexes</c> management routes address the index itself, as they do in Azure.
/// </remarks>
public class SearchAlias
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The one index the alias resolves to.
    /// </summary>
    public List<string> Indexes { get; set; } = [];

    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; set; }

    /// <remarks>
    /// Preserves properties the emulator does not model, for the reason given on
    /// <see cref="SynonymMap.AdditionalProperties"/>.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
