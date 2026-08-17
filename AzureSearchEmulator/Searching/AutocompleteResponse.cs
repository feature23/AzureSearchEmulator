namespace AzureSearchEmulator.Searching;

/// <summary>
/// The result of a <c>docs/autocomplete</c> call (issue #45).
/// </summary>
public class AutocompleteResponse
{
    /// <summary>
    /// The percentage of the index searched, or null when the request did not ask for it by
    /// supplying <c>minimumCoverage</c>. See <see cref="SearchCoverage"/>.
    /// </summary>
    public double? Coverage { get; set; }

    public IList<AutocompleteItem> Results { get; set; } = new List<AutocompleteItem>();
}

/// <summary>
/// A single completion.
/// </summary>
/// <param name="Text">The completed term or terms on their own.</param>
/// <param name="QueryPlusText">
/// The caller's search text with the incomplete final term replaced by the completion, which
/// is what a typeahead box puts back in the input.
/// </param>
public record AutocompleteItem(string Text, string QueryPlusText);
