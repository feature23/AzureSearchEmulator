using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// The vector search configuration of an index: the algorithms available and the profiles
/// fields bind to (issue #46).
/// </summary>
/// <remarks>
/// A vector field does not name an algorithm directly. It names a profile, and the profile
/// names an algorithm; the indirection is what lets several fields share one algorithm
/// configuration, and it is the shape the Azure SDK serializes, so the emulator models it the
/// same way rather than flattening it.
///
/// <c>vectorizers</c> and <c>compressions</c> are deliberately not modelled. Both describe
/// work the emulator cannot do — a vectorizer calls a hosted embedding model, and a
/// compression changes the stored representation to trade recall for size — so they are
/// captured in <see cref="AdditionalProperties"/> and preserved verbatim but inert.
///
/// That bag is not optional. Before <c>vectorSearch</c> was modelled the whole object rode
/// through <see cref="SearchIndex.AdditionalProperties"/> and everything inside it survived by
/// default; modelling it means anything not declared here is dropped instead, so a client that
/// read a real-service definition, changed one field and wrote it back would find its
/// vectorizers deleted. Every type in this file carries a bag for the same reason —
/// <see cref="JsonExtensionDataAttribute"/> applies per type rather than recursively.
/// </remarks>
public class VectorSearch
{
    /// <summary>
    /// The named algorithm configurations profiles can refer to.
    /// </summary>
    public IList<VectorSearchAlgorithm> Algorithms { get; set; } = new List<VectorSearchAlgorithm>();

    /// <summary>
    /// The named profiles a field's <see cref="SearchField.VectorSearchProfile"/> binds to.
    /// </summary>
    public IList<VectorSearchProfile> Profiles { get; set; } = new List<VectorSearchProfile>();

    /// <summary>
    /// Finds the profile a field named, or null when the index defines none by that name.
    /// </summary>
    /// <remarks>
    /// Matched case-insensitively, as Azure matches the other named parts of an index
    /// definition.
    /// </remarks>
    public VectorSearchProfile? FindProfile(string name)
        => Profiles.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the algorithm a profile named, or null when the index defines none by that name.
    /// </summary>
    public VectorSearchAlgorithm? FindAlgorithm(string name)
        => Algorithms.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves the similarity metric a field's profile selects, or null when the field binds
    /// to no usable profile.
    /// </summary>
    /// <remarks>
    /// The metric lives on the algorithm's parameter object rather than the algorithm itself,
    /// and which parameter object holds it depends on the kind, so resolving it is worth doing
    /// in one place. A profile naming a missing algorithm returns null rather than throwing:
    /// <see cref="Indexing.VectorSearchValidator"/> rejects that at definition time, so by the
    /// time a query resolves a metric the combination is already known to be sound.
    /// </remarks>
    public VectorSearchMetric? ResolveMetric(string profileName)
    {
        if (FindProfile(profileName) is not { } profile
            || FindAlgorithm(profile.Algorithm) is not { } algorithm)
        {
            return null;
        }

        return algorithm.GetMetric();
    }

    /// <summary>
    /// Properties the emulator does not model, kept verbatim so they survive a get-modify-put
    /// cycle (issues #41 and #46).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// One named algorithm configuration.
/// </summary>
/// <remarks>
/// Azure discriminates on <c>kind</c> and puts the tuning knobs in a parameter object named
/// after it. The emulator accepts both kinds and implements both as an exhaustive scan, so the
/// only part of the configuration that changes a result is the metric — see
/// <see cref="Searching.VectorSearchSupport"/> for why that is a defensible emulation.
/// </remarks>
public class VectorSearchAlgorithm
{
    /// <summary>
    /// The name a <see cref="VectorSearchProfile"/> refers to.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Which algorithm this configures.
    /// </summary>
    public VectorSearchAlgorithmKind Kind { get; set; } = VectorSearchAlgorithmKind.Hnsw;

    /// <summary>
    /// Tuning for <see cref="VectorSearchAlgorithmKind.Hnsw"/>, ignored for the other kind.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HnswParameters? HnswParameters { get; set; }

    /// <summary>
    /// Tuning for <see cref="VectorSearchAlgorithmKind.ExhaustiveKnn"/>, ignored for the other
    /// kind.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExhaustiveKnnParameters? ExhaustiveKnnParameters { get; set; }

    /// <summary>
    /// The metric this algorithm's parameters select, defaulting to
    /// <see cref="VectorSearchMetric.Cosine"/> as Azure does.
    /// </summary>
    /// <remarks>
    /// The parameter object is optional in the JSON, and a kind's parameters may be absent or
    /// belong to the other kind, so this reads the one matching <see cref="Kind"/> and falls
    /// back to the default rather than trusting either to be present.
    /// </remarks>
    public VectorSearchMetric GetMetric()
        => Kind switch
        {
            VectorSearchAlgorithmKind.ExhaustiveKnn => ExhaustiveKnnParameters?.Metric,
            _ => HnswParameters?.Metric
        } ?? VectorSearchMetric.Cosine;

    /// <summary>
    /// Properties the emulator does not model, kept verbatim so they survive a get-modify-put
    /// cycle (issues #41 and #46).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Tuning for the HNSW graph.
/// </summary>
/// <remarks>
/// Every property here except the metric is accepted and ignored. The emulator answers a
/// vector query by exhaustive scan, which has no graph to tune, and the issue asks for these
/// to be accepted rather than rejected so that an index definition written for the real
/// service is usable unchanged. Retaining them also keeps them in the definition the emulator
/// writes back.
/// </remarks>
public class HnswParameters
{
    /// <summary>
    /// Bi-directional link count per node. Accepted and ignored.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? M { get; set; }

    /// <summary>
    /// Candidate list size during graph construction. Accepted and ignored.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EfConstruction { get; set; }

    /// <summary>
    /// Candidate list size during search. Accepted and ignored.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EfSearch { get; set; }

    /// <summary>
    /// The similarity metric, which the emulator does honour.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VectorSearchMetric? Metric { get; set; }

    /// <summary>
    /// Properties the emulator does not model, kept verbatim so they survive a get-modify-put
    /// cycle (issues #41 and #46).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Tuning for exhaustive k-nearest-neighbour search.
/// </summary>
public class ExhaustiveKnnParameters
{
    /// <summary>
    /// The similarity metric.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VectorSearchMetric? Metric { get; set; }

    /// <summary>
    /// Properties the emulator does not model, kept verbatim so they survive a get-modify-put
    /// cycle (issues #41 and #46).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// One named profile, binding a field to an algorithm.
/// </summary>
public class VectorSearchProfile
{
    /// <summary>
    /// The name a field's <see cref="SearchField.VectorSearchProfile"/> refers to.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The <see cref="VectorSearchAlgorithm.Name"/> this profile selects.
    /// </summary>
    public string Algorithm { get; set; } = "";

    /// <summary>
    /// The vectorizer that would turn query text into a vector.
    /// </summary>
    /// <remarks>
    /// Kept so the profile round-trips, but unusable: a vectorizer calls a hosted embedding
    /// model, so a query relying on one is rejected rather than answered wrongly.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vectorizer { get; set; }

    /// <summary>
    /// The compression configuration applied to this profile's vectors.
    /// </summary>
    /// <remarks>
    /// Kept for round-tripping and otherwise ignored; the emulator stores vectors uncompressed,
    /// which can only make its results more exact than the service's.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Compression { get; set; }

    /// <summary>
    /// Properties the emulator does not model, kept verbatim so they survive a get-modify-put
    /// cycle (issues #41 and #46).
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// The algorithm kinds Azure accepts.
/// </summary>
[JsonConverter(typeof(CamelCaseEnumConverter<VectorSearchAlgorithmKind>))]
public enum VectorSearchAlgorithmKind
{
    /// <summary>
    /// Hierarchical Navigable Small World, an approximate nearest-neighbour graph.
    /// </summary>
    Hnsw,

    /// <summary>
    /// Exhaustive k-nearest-neighbour, which scans every vector.
    /// </summary>
    ExhaustiveKnn
}

/// <summary>
/// The similarity metrics Azure accepts for a <c>Collection(Edm.Single)</c> field.
/// </summary>
/// <remarks>
/// Azure's metric enum has a fourth value, <c>hamming</c>, which its specification restricts to
/// bit-packed binary data — a <c>Collection(Edm.Byte)</c> field declaring
/// <c>vectorEncoding: "packedBit"</c>. The emulator does not support that element type, so a
/// metric only usable with it would have nothing to apply to; it is left out rather than
/// accepted and quietly treated as something else. <see cref="VectorSearchJson"/> reports it
/// as unsupported rather than unrecognized, so the message names the real reason.
/// </remarks>
[JsonConverter(typeof(VectorSearchMetricConverter))]
public enum VectorSearchMetric
{
    /// <summary>
    /// Cosine similarity, the default and by far the most common choice.
    /// </summary>
    Cosine,

    /// <summary>
    /// Euclidean (L2) distance, where smaller is closer.
    /// </summary>
    Euclidean,

    /// <summary>
    /// Dot product, which equals cosine similarity for normalized vectors.
    /// </summary>
    DotProduct
}

/// <summary>
/// Reads and writes <see cref="VectorSearchMetric"/>, distinguishing a metric the emulator does
/// not support from one that does not exist.
/// </summary>
/// <remarks>
/// <c>hamming</c> is a real Azure metric, so reporting it the way a typo is reported — "not a
/// valid metric; expected one of cosine, euclidean, dotProduct" — would send someone looking for
/// a spelling mistake in a value they had spelled correctly. It is unsupported here because the
/// bit-packed binary element type it applies to is unsupported, and the message says so.
/// </remarks>
public class VectorSearchMetricConverter : JsonConverter<VectorSearchMetric>
{
    /// <summary>
    /// The metric Azure defines for bit-packed binary vectors, which the emulator has no element
    /// type to use it with.
    /// </summary>
    private const string HammingMetric = "hamming";

    public override VectorSearchMetric Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString();

        if (string.Equals(value, HammingMetric, StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(
                "The 'hamming' metric applies only to bit-packed binary vectors, which are not " +
                "supported. Use cosine, euclidean or dotProduct with a " +
                "Collection(Edm.Single) field.");
        }

        if (value != null && Enum.TryParse<VectorSearchMetric>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new JsonException(
            $"'{value}' is not a valid vector search metric; expected one of " +
            $"{string.Join(", ", Enum.GetNames<VectorSearchMetric>().Select(ToCamelCase))}.");
    }

    public override void Write(Utf8JsonWriter writer, VectorSearchMetric value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToCamelCase(value.ToString()));

    private static string ToCamelCase(string name)
        => string.IsNullOrEmpty(name)
            ? name
            : char.ToLower(name[0], CultureInfo.InvariantCulture) + name[1..];
}
