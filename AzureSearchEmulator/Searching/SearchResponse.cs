using System.Text.Json.Nodes;
using AzureSearchEmulator.Semantic;

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
    /// The extracted answers of a semantic query that asked for them, or null when none were
    /// asked for (issue #109).
    /// </summary>
    public IReadOnlyList<SemanticAnswer>? Answers { get; set; }

    /// <summary>
    /// The <c>skip</c> for the next page, or null when there is no next page to offer.
    /// </summary>
    public int? NextPageSkip { get; set; }
}