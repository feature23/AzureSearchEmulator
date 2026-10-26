using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// The search page's inputs, as typed (issue #92).
/// </summary>
/// <remarks>
/// Each text box takes the parameter's syntax as the REST API documents it, so a query that works
/// here can be pasted into a client unchanged and vice versa. Blank means "not sent" rather than
/// "sent empty": an empty <c>$filter</c> is a syntax error, not an absent filter.
/// </remarks>
public class SearchQueryForm
{
    public const int DefaultTop = 10;

    public string SearchText { get; set; } = "";

    /// <summary><c>simple</c>, <c>full</c> or <c>semantic</c>.</summary>
    public string QueryType { get; set; } = "simple";

    /// <summary><c>any</c> or <c>all</c>.</summary>
    public string SearchMode { get; set; } = "any";

    public string Filter { get; set; } = "";

    public string OrderBy { get; set; } = "";

    public string Select { get; set; } = "";

    public string SearchFields { get; set; } = "";

    public string Facets { get; set; } = "";

    public string Highlight { get; set; } = "";

    public string ScoringProfile { get; set; } = "";

    public string SemanticConfiguration { get; set; } = "";

    public int Top { get; set; } = DefaultTop;

    public int Skip { get; set; }

    public bool IncludeTotalCount { get; set; } = true;

    public bool IsSemantic => QueryType == "semantic";

    public SearchOptions ToSearchOptions()
    {
        var options = new SearchOptions
        {
            Size = Top,
            Skip = Skip > 0 ? Skip : null,
            IncludeTotalCount = IncludeTotalCount,
            Filter = NullIfBlank(Filter),
            ScoringProfile = NullIfBlank(ScoringProfile),
            QueryType = QueryType switch
            {
                "full" => SearchQueryType.Full,
                "semantic" => SearchQueryType.Semantic,
                _ => SearchQueryType.Simple
            },
            SearchMode = SearchMode == "all" ? Azure.Search.Documents.Models.SearchMode.All : Azure.Search.Documents.Models.SearchMode.Any
        };

        // The SDK takes these as lists and joins them back with commas on the wire, so the split
        // only has to agree with that join, not interpret the expressions.
        AddAll(options.OrderBy, SplitList(OrderBy));
        AddAll(options.Select, SplitList(Select));
        AddAll(options.SearchFields, SplitList(SearchFields));
        AddAll(options.HighlightFields, SplitList(Highlight));
        AddAll(options.Facets, SplitFacets(Facets));

        if (IsSemantic)
        {
            options.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = NullIfBlank(SemanticConfiguration),
                QueryCaption = new QueryCaption(QueryCaptionType.Extractive),
                QueryAnswer = new QueryAnswer(QueryAnswerType.Extractive)
            };
        }

        return options;
    }

    /// <summary>
    /// Splits a comma-separated list on the commas that separate its items.
    /// </summary>
    /// <remarks>
    /// Commas inside parentheses or quotes belong to the item: <c>geo.distance(location,
    /// geography'POINT(-122 47)') asc, rating desc</c> is two <c>$orderby</c> clauses, not three.
    /// </remarks>
    public static IReadOnlyList<string> SplitList(string value)
    {
        var result = new List<string>();
        var depth = 0;
        var inQuote = false;
        var start = 0;

        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : ',';

            switch (c)
            {
                case '\'':
                    inQuote = !inQuote;
                    break;
                case '(' when !inQuote:
                    depth++;
                    break;
                case ')' when !inQuote && depth > 0:
                    depth--;
                    break;
                case ',' when (!inQuote && depth == 0) || i == value.Length:
                    var item = value[start..i].Trim();

                    if (item.Length > 0)
                    {
                        result.Add(item);
                    }

                    start = i + 1;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Splits a comma-separated list of facet expressions.
    /// </summary>
    /// <remarks>
    /// A facet expression carries its own parameters after commas — <c>rating,count:5,sort:value</c>
    /// — so the list cannot simply be split on commas. A segment containing a colon is a parameter
    /// and stays with the field before it, which lets <c>category, rating,interval:1</c> mean the two
    /// facets it reads as.
    /// </remarks>
    public static IReadOnlyList<string> SplitFacets(string facets)
    {
        var result = new List<string>();

        foreach (var segment in facets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Contains(':') && result.Count > 0)
            {
                result[^1] += "," + segment;
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    private static void AddAll(IList<string> target, IEnumerable<string> items)
    {
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string? NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
