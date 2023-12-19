namespace AzureSearchEmulator.Models;

public class SearchIndex
{
    public string Name { get; init; } = "";

    public IList<SearchField> Fields { get; init; } = new List<SearchField>();

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
