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

    /// <summary>
    /// The percentage of the index searched, or null when the request did not ask for it by
    /// supplying <c>minimumCoverage</c>. See <see cref="SearchCoverage"/>.
    /// </summary>
    public double? Coverage { get; set; }

    public IList<JsonObject> Results { get; set; } = new List<JsonObject>();

    /// <summary>
    /// The <c>skip</c> for the next page, or null when there is no next page to offer.
    /// </summary>
    public int? NextPageSkip { get; set; }
}