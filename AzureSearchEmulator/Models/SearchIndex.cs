namespace AzureSearchEmulator.Models;

public class SearchIndex
{
    public string Name { get; set; } = "";
    
    public IList<SearchField> Fields { get; set; } = new List<SearchField>();
}