namespace AzureSearchEmulator.Searching;

/// <summary>
/// Computes the <c>@search.coverage</c> value reported for a query (issue #39).
/// </summary>
/// <remarks>
/// Coverage is the percentage of the index that was actually searched. In Azure it can fall
/// below 100 when an index is spread across replicas and one of them is unavailable, and
/// <c>minimumCoverage</c> is the floor a caller will accept before treating the result as a
/// failure.
///
/// The emulator searches a single local index, which is either fully available or not
/// available at all, so coverage is always total. That makes any <c>minimumCoverage</c> floor
/// genuinely met rather than ignored, and the value reported back is a fact about this
/// emulator rather than a placeholder.
///
/// A caller's degraded-coverage branch therefore never runs locally. That is a real limit,
/// but it is a limit of running one replica rather than a divergence in the response: Azure
/// with a single healthy replica answers exactly the same way.
/// </remarks>
public static class SearchCoverage
{
    /// <summary>
    /// The coverage a single local index always achieves.
    /// </summary>
    public const double Full = 100.0;

    /// <summary>
    /// Returns the coverage to report, or null when the response should omit
    /// <c>@search.coverage</c> entirely.
    /// </summary>
    /// <remarks>
    /// Azure includes the field only when the request supplied <c>minimumCoverage</c>, and the
    /// SDK surfaces its absence as a null <c>SearchResults.Coverage</c>. Emitting it
    /// unconditionally would make a caller's null check unreachable locally while it still
    /// fires against the real service.
    /// </remarks>
    public static double? GetCoverage(SearchRequest request)
        => request.MinimumCoverage == null ? null : Full;
}
