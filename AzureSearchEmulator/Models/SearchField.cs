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

    /// <summary>
    /// The normalizer applied to this field's filter, facet and sort values (issue #74).
    /// </summary>
    /// <remarks>
    /// Null means none, which is Azure's default and leaves the value compared exactly as it
    /// was written. Applies only to <c>Edm.String</c> and <c>Collection(Edm.String)</c> fields
    /// that are filterable, sortable or facetable — a normalizer is what gives those operations
    /// the case and accent folding that analysis gives a searchable field, and there is nothing
    /// for it to do on a field none of them can reach.
    /// <see cref="Indexing.NormalizerValidator"/> enforces that.
    ///
    /// It does not affect full-text search: a searchable field's own analyzer still decides
    /// how its tokens are produced, so the two are independent and a field may carry both.
    /// </remarks>
    public string? Normalizer { get; set; }

    public IList<string> SynonymMaps { get; set; } = new List<string>();

    /// <summary>
    /// Sub-fields of an <c>Edm.ComplexType</c> or <c>Collection(Edm.ComplexType)</c> field.
    /// </summary>
    public IList<SearchField> Fields { get; set; } = new List<SearchField>();

    /// <summary>
    /// The length of the vectors a <c>Collection(Edm.Single)</c> field holds (issue #46).
    /// </summary>
    /// <remarks>
    /// Null on every other field type. Declaring it is what makes a float collection a vector
    /// field rather than an ordinary collection of numbers, and it is fixed for the life of
    /// the index: documents are rejected when their vector does not match it, so allowing it
    /// to change would leave the already-indexed documents disagreeing with the schema.
    /// <see cref="Indexing.IndexSchemaChangeValidator"/> enforces that.
    ///
    /// Omitted when null rather than written as <c>"dimensions": null</c>. These two properties
    /// are meaningless on the great majority of fields, and the serializer writes nulls by
    /// default, so without this every field of every index would grow two null properties that
    /// Azure's own definitions do not carry.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }

    /// <summary>
    /// The <see cref="VectorSearchProfile.Name"/> this field's vectors are searched under
    /// (issue #46).
    /// </summary>
    /// <remarks>
    /// The profile supplies the similarity metric. A vector field must name one, because
    /// without it there is no metric and so no defined ordering for a query against the field.
    ///
    /// Omitted when null, for the reason given on <see cref="Dimensions"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VectorSearchProfile { get; set; }

    /// <summary>
    /// Field properties the emulator does not model, kept verbatim so they survive a
    /// get-modify-put cycle (issue #41).
    /// </summary>
    /// <remarks>
    /// Field-level properties are dropped by the same round-trip that loses the index-level
    /// ones — <c>normalizer</c> among them. Because the attribute applies per type rather than
    /// recursively, sub-fields of a complex field need it here to be covered too.
    ///
    /// Uses the same nullable <see cref="JsonElement"/> dictionary as
    /// <see cref="SearchIndex.AdditionalProperties"/>, for the reasons given there.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
