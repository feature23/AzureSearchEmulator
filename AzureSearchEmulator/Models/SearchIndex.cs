namespace AzureSearchEmulator.Models;

public class SearchIndex
{
    public string Name { get; init; } = "";

    public IList<SearchField> Fields { get; init; } = new List<SearchField>();

    /// <summary>
    /// Suggesters available for <c>docs/suggest</c> and <c>docs/autocomplete</c> (issue #45).
    /// </summary>
    public IList<SearchSuggester> Suggesters { get; init; } = new List<SearchSuggester>();

    /// <summary>
    /// Finds the suggester a request named, matching case-insensitively as Azure Search does,
    /// or null when the index defines no suggester by that name.
    /// </summary>
    public SearchSuggester? FindSuggester(string name)
        => Suggesters.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public SearchField GetKeyField()
    {
        var keys = Fields.Where(i => i.Key.GetValueOrDefault()).ToList();

        return keys.Count switch
        {
            0 => throw new InvalidOperationException("Index does not have a configured key"),
            > 1 => throw new InvalidOperationException("Index has more than one configured key"),
            _ => keys[0]
        };
    }
}
