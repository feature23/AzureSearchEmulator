using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Searching;

public class SearchResponse
{
    public int? Count { get; set; }

    /// <summary>
    /// The facet buckets counted for this query, keyed by field path, or null when no facets
    /// were requested.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FacetBucket>>? Facets { get; set; }

    public IList<JsonObject> Results { get; set; } = new List<JsonObject>();
}