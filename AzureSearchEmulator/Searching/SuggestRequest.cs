namespace AzureSearchEmulator.Searching;

/// <summary>
/// A <c>docs/suggest</c> request (issue #45).
/// </summary>
public class SuggestRequest
{
    /// <summary>
    /// The partial term the caller has typed so far.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Names which suggester on the index to draw from.
    /// </summary>
    public string? SuggesterName { get; set; }

    public string? Filter { get; set; }

    /// <summary>
    /// Allows suggestions to be found despite a typo in the search text.
    /// </summary>
    public bool Fuzzy { get; set; }

    public string? HighlightPreTag { get; set; }

    public string? HighlightPostTag { get; set; }

    public double? MinimumCoverage { get; set; }

    public string? Orderby { get; set; }

    public string? Select { get; set; }

    /// <summary>
    /// Azure Search caps suggestions at 100 and defaults to 5, unlike a search's 50.
    /// </summary>
    public int Top { get; set; } = 5;

    /// <summary>
    /// Restricts which of the suggester's source fields are matched against.
    /// </summary>
    public string? SearchFields { get; set; }
}
