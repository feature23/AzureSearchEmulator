using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

public class SearchField
{
    [Required]
    public string Name { get; set; } = "";

    [Required]
    public string Type { get; set; } = "";

    public bool? Searchable { get; set; }

    public bool Filterable { get; set; } = true;

    public bool Hidden
    {
        get => !Retrievable;
        set => Retrievable = !value;
    }

    public bool Retrievable { get; set; } = true;

    public bool? Sortable { get; set; }

    public bool? Facetable { get; set; }

    public bool? Key { get; set; }

    public string? Analyzer { get; set; }

    public string? SearchAnalyzer { get; set; }

    public string? IndexAnalyzer { get; set; }

    public IList<string> SynonymMaps { get; set; } = new List<string>();

    /// <summary>
    /// Sub-fields of an <c>Edm.ComplexType</c> or <c>Collection(Edm.ComplexType)</c> field.
    /// </summary>
    public IList<SearchField> Fields { get; set; } = new List<SearchField>();

    /// <summary>
    /// Field properties the emulator does not model, kept verbatim so they survive a
    /// get-modify-put cycle (issue #41).
    /// </summary>
    /// <remarks>
    /// Field-level properties are dropped by the same round-trip that loses the index-level
    /// ones — <c>dimensions</c>, <c>vectorSearchProfile</c> and <c>normalizer</c> among them.
    /// Because the attribute applies per type rather than recursively, sub-fields of a complex
    /// field need it here to be covered too.
    ///
    /// Uses the same nullable <see cref="JsonElement"/> dictionary as
    /// <see cref="SearchIndex.AdditionalProperties"/>, for the reasons given there.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
