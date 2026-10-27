using Azure.Search.Documents.Indexes.Models;

namespace AzureSearchEmulator.Dashboard;

/// <summary>One row of the index page's field table (issue #91).</summary>
/// <param name="Path">The field's name, prefixed with its parents' for a subfield of a complex field.</param>
/// <param name="Depth">How many complex fields this one is nested inside.</param>
/// <param name="Retrievable">Null when the definition leaves it unstated, as with the other attributes.</param>
public record IndexFieldRow(
    string Path,
    int Depth,
    string Type,
    bool IsKey,
    bool IsComplex,
    bool? Retrievable,
    bool? Searchable,
    bool? Filterable,
    bool? Sortable,
    bool? Facetable,
    string? Analyzer,
    int? Dimensions,
    string? VectorProfile);

/// <summary>
/// Lays out an SDK index definition for the dashboard's index pages (issue #91).
/// </summary>
public static class IndexSchema
{
    /// <summary>
    /// Every field in definition order, with each complex field's subfields straight after it, so
    /// the table reads like the definition does.
    /// </summary>
    public static IReadOnlyList<IndexFieldRow> Flatten(IEnumerable<SearchField> fields)
    {
        var rows = new List<IndexFieldRow>();
        AddFields(rows, fields, parent: null, depth: 0);
        return rows;
    }

    public static string? KeyField(SearchIndex index) =>
        index.Fields.FirstOrDefault(i => i.IsKey == true)?.Name;

    /// <summary>
    /// Whether documents can be paged in key order. Without it they come in the order the emulator
    /// returns a match-all search, which is stable between requests but not meaningful.
    /// </summary>
    public static bool IsKeySortable(SearchIndex index) =>
        index.Fields.FirstOrDefault(i => i.IsKey == true)?.IsSortable == true;

    private static void AddFields(List<IndexFieldRow> rows, IEnumerable<SearchField> fields, string? parent, int depth)
    {
        foreach (var field in fields)
        {
            var path = parent is null ? field.Name : $"{parent}/{field.Name}";
            var isComplex = field.Fields.Count > 0 || field.Type.ToString().Contains("ComplexType", StringComparison.Ordinal);

            rows.Add(new IndexFieldRow(
                path,
                depth,
                field.Type.ToString(),
                field.IsKey == true,
                isComplex,
                field.IsHidden is { } hidden ? !hidden : null,
                field.IsSearchable,
                field.IsFilterable,
                field.IsSortable,
                field.IsFacetable,
                DescribeAnalyzer(field),
                field.VectorSearchDimensions,
                field.VectorSearchProfileName));

            AddFields(rows, field.Fields, path, depth + 1);
        }
    }

    /// <summary>
    /// The analyzer a field uses, or the pair it uses when indexing and searching differ.
    /// </summary>
    private static string? DescribeAnalyzer(SearchField field)
    {
        if (field.AnalyzerName is { } analyzer)
        {
            return analyzer.ToString();
        }

        return (field.IndexAnalyzerName, field.SearchAnalyzerName) switch
        {
            (null, null) => null,
            ({ } index, { } search) when index == search => index.ToString(),
            (var index, var search) => $"index: {index?.ToString() ?? "default"}, search: {search?.ToString() ?? "default"}"
        };
    }
}
