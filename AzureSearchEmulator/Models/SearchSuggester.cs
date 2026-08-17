using System.ComponentModel.DataAnnotations;

namespace AzureSearchEmulator.Models;

/// <summary>
/// A named suggester on an index, identifying the fields that typeahead queries draw from
/// (issue #45).
/// </summary>
/// <remarks>
/// Azure Search builds a suggester from an edge n-gram index at index time, which is what
/// makes a prefix match cheap there. The emulator has no such side index and matches with a
/// prefix query at query time instead; the definition still matters because it names which
/// fields a <c>suggest</c> or <c>autocomplete</c> call is allowed to look at.
/// </remarks>
public class SearchSuggester
{
    /// <summary>
    /// The name callers pass as <c>suggesterName</c>.
    /// </summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>
    /// The only value Azure Search accepts here, retained so a definition round-trips intact.
    /// </summary>
    public string SearchMode { get; set; } = "analyzingInfixMatching";

    /// <summary>
    /// The fields this suggester draws its suggestions from.
    /// </summary>
    public IList<string> SourceFields { get; set; } = new List<string>();
}
