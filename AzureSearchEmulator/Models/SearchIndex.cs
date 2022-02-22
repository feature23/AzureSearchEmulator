namespace AzureSearchEmulator.Models;

public class SearchIndex
{
    public string Name { get; set; } = "";
    
    public IList<SearchField> Fields { get; set; } = new List<SearchField>();

    public SearchField GetKeyField()
    {
        var keys = Fields.Where(i => i.Key.GetValueOrDefault()).ToList();

        if (keys.Count == 0)
        {
            throw new InvalidOperationException("Index does not have a configured key");
        }

        if (keys.Count > 1)
        {
            throw new InvalidOperationException("Index has more than one configured key");
        }

        return keys[0];
    }
}