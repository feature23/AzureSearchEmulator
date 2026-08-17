namespace AzureSearchEmulator.Searching;

/// <summary>
/// Detects search parameters the emulator binds but does not act on, so that a request using
/// one is refused rather than answered with results that quietly differ from Azure's
/// (issue #39).
/// </summary>
/// <remarks>
/// Silent divergence is the worst failure mode for an emulator: the caller's code passes
/// locally and fails against the real service, with nothing in the local run pointing at the
/// parameter that was dropped. Refusing outright is the same principle applied to
/// <c>Collection(Edm.GeographyPoint)</c> before it was implemented — fail loudly, and let
/// each parameter stop being refused as it is genuinely supported.
///
/// Four of the six parameters the issue named are not refused here. <c>facets</c> and
/// <c>select</c> are implemented. <c>minimumCoverage</c> and <c>sessionId</c> describe how a
/// query is distributed across replicas, and a single local index satisfies both exactly
/// rather than approximately — so answering them is faithful, not a silent divergence.
/// </remarks>
public static class UnsupportedSearchParameters
{
    /// <summary>
    /// Returns a message naming every unsupported parameter the request uses, or null when
    /// the request uses none of them.
    /// </summary>
    /// <remarks>
    /// All offending parameters are reported together rather than just the first, so a caller
    /// clearing them out learns the full set in one round trip instead of one per attempt.
    /// </remarks>
    public static string? GetRejectionMessage(SearchRequest request)
    {
        var unsupported = new List<string>();

        if (!string.IsNullOrEmpty(request.ScoringProfile))
        {
            unsupported.Add("scoringProfile");
        }

        // An empty list is what the SDK sends for "no scoring parameters", and carries no
        // request to score differently, so only a populated one is a refusal.
        if (request.ScoringParameters is { Count: > 0 })
        {
            unsupported.Add("scoringParameters");
        }

        // minimumCoverage and sessionId are deliberately absent from this list: both describe
        // how a query is spread across replicas, and a single local index answers both
        // faithfully rather than approximately. See SearchCoverage for minimumCoverage, which
        // is genuinely satisfied and reported; sessionId asks only that repeated queries be
        // routed to the same replica, which is trivially true when there is one.

        if (unsupported.Count == 0)
        {
            return null;
        }

        return $"The following search {(unsupported.Count == 1 ? "parameter is" : "parameters are")} " +
               $"not supported by this emulator: {string.Join(", ", unsupported)}. " +
               "Remove them from the request, or use the real Azure Search service if you need them.";
    }
}
