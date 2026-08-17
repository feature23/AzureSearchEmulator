namespace AzureSearchEmulator.Searching;

/// <summary>
/// A <c>docs/autocomplete</c> request (issue #45).
/// </summary>
public class AutocompleteRequest
{
    /// <summary>
    /// The partial term the caller has typed so far.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Names which suggester on the index to draw from.
    /// </summary>
    public string? SuggesterName { get; set; }

    /// <summary>
    /// One of <c>oneTerm</c>, <c>twoTerms</c>, or <c>oneTermWithContext</c>.
    /// </summary>
    public string AutocompleteMode { get; set; } = AutocompleteModes.OneTerm;

    public string? Filter { get; set; }

    /// <summary>
    /// Allows completions to be found despite a typo in the search text.
    /// </summary>
    public bool Fuzzy { get; set; }

    public string? HighlightPreTag { get; set; }

    public string? HighlightPostTag { get; set; }

    public double? MinimumCoverage { get; set; }

    /// <summary>
    /// Azure Search caps completions at 100 and defaults to 5.
    /// </summary>
    public int Top { get; set; } = 5;

    /// <summary>
    /// Restricts which of the suggester's source fields are matched against.
    /// </summary>
    public string? SearchFields { get; set; }
}

/// <summary>
/// The <c>autocompleteMode</c> values Azure Search accepts.
/// </summary>
public static class AutocompleteModes
{
    public const string OneTerm = "oneTerm";
    public const string TwoTerms = "twoTerms";
    public const string OneTermWithContext = "oneTermWithContext";

    public static bool IsValid(string? mode) =>
        mode is not null
        && (mode.Equals(OneTerm, StringComparison.OrdinalIgnoreCase)
            || mode.Equals(TwoTerms, StringComparison.OrdinalIgnoreCase)
            || mode.Equals(OneTermWithContext, StringComparison.OrdinalIgnoreCase));
}
