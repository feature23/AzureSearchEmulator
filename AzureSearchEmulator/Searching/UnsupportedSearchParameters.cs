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
/// <c>facets</c> and <c>select</c> were on this list when the issue was filed and are not
/// here now: both are implemented, so both are answered rather than refused.
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

        // The emulator searches a single local index, so coverage is always total and the
        // default of 100 is genuinely met rather than ignored. Only a caller deliberately
        // accepting partial coverage is asking for behaviour that does not exist here.
        if (request.MinimumCoverage is < 100)
        {
            unsupported.Add("minimumCoverage");
        }

        // Sticky sessions target one replica out of several; with a single index there is no
        // routing decision to make, but honouring it silently would still mislead a caller
        // testing that their session pinning works.
        if (!string.IsNullOrEmpty(request.SessionId))
        {
            unsupported.Add("sessionId");
        }

        if (unsupported.Count == 0)
        {
            return null;
        }

        return $"The following search {(unsupported.Count == 1 ? "parameter is" : "parameters are")} " +
               $"not supported by this emulator: {string.Join(", ", unsupported)}. " +
               "Remove them from the request, or use the real Azure Search service if you need them.";
    }
}
