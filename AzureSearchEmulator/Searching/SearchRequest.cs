namespace AzureSearchEmulator.Searching;

public class SearchRequest
{
    public bool Count { get; set; }

    public IList<string>? Facets { get; set; }

    public string? Filter { get; set; }

    public string? Highlight { get; set; }

    public string? HighlightPreTag { get; set; }

    public string? HighlightPostTag { get; set; }

    public double? MinimumCoverage { get; set; }

    public string? Orderby { get; set; }

    // TODO.PI: make this an enum
    public string QueryType { get; set; } = "simple";

    public IList<string>? ScoringParameters { get; set; }

    public string? ScoringProfile { get; set; }

    // TODO.PI: make this an enum
    public string ScoringStatistics { get; set; } = "local";

    public string? Search { get; set; }

    public string? SearchFields { get; set; }

    // TODO.PI: make this an enum
    public string SearchMode { get; set; } = "any";

    public string? Select { get; set; }

    public string? SessionId { get; set; }

    public int Skip { get; set; } = 0;

    public int Top { get; set; } = 50;
}