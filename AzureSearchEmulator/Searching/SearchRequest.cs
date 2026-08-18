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

    /// <summary>
    /// Vector queries to run against the index's vector fields (issue #46).
    /// </summary>
    /// <remarks>
    /// Bound but not yet answered: this phase adds the index-definition and storage half of
    /// vector search, and a request carrying one is refused rather than silently ignored.
    /// Leaving it unbound would drop it during model binding and return ordinary text results
    /// as though the vector query had been honoured, which is the silent-divergence failure
    /// issue #39 set out to eliminate.
    /// </remarks>
    public IList<VectorQuery>? VectorQueries { get; set; }

    /// <summary>
    /// How <c>$filter</c> combines with a vector query.
    /// </summary>
    /// <remarks>
    /// Bound for the same reason as <see cref="VectorQueries"/>, and unused until vector
    /// queries are answered.
    /// </remarks>
    public string? VectorFilterMode { get; set; }
}