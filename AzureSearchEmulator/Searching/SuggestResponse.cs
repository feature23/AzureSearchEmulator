using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// The result of a <c>docs/suggest</c> call (issue #45).
/// </summary>
public class SuggestResponse
{
    /// <summary>
    /// The percentage of the index searched, or null when the request did not ask for it by
    /// supplying <c>minimumCoverage</c>. See <see cref="SearchCoverage"/>.
    /// </summary>
    public double? Coverage { get; set; }

    /// <summary>
    /// One entry per matching document, each carrying its <c>@search.text</c> alongside the
    /// selected document fields.
    /// </summary>
    public IList<JsonObject> Results { get; set; } = new List<JsonObject>();
}
