using System.ComponentModel.DataAnnotations;

namespace AzureSearchEmulator.Models;

public class SearchField
{
    [Required]
    public string Name { get; set; } = "";

    [Required]
    public string Type { get; set; } = "";

    public bool? Searchable { get; set; }

    public bool Filterable { get; set; } = true;

    public bool Hidden
    {
        get => !Retrievable;
        set => Retrievable = !value;
    }

    public bool Retrievable { get; set; } = true;

    public bool? Sortable { get; set; }

    public bool? Facetable { get; set; }

    public bool? Key { get; set; }

    public string? Analyzer { get; set; }

    public string? SearchAnalyzer { get; set; }

    public string? IndexAnalyzer { get; set; }

    public IList<string> SynonymMaps { get; set; } = new List<string>();

    public IList<SearchField> Fields { get; } = new List<SearchField>();
}
