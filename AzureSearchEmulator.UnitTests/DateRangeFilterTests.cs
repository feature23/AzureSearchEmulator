using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers <c>gt</c>, <c>ge</c>, <c>lt</c> and <c>le</c> on an <c>Edm.DateTimeOffset</c> field,
/// which previously failed with "comparison not supported for type DateTimeOffset".
/// </summary>
/// <remarks>
/// Document 2 is dated exactly on the boundary used by every test, so each test also shows
/// whether its operator includes or excludes the boundary.
/// </remarks>
public class DateRangeFilterTests : IDisposable
{
    private const string Boundary = "2024-06-01T00:00:00Z";

    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    public DateRangeFilterTests()
    {
        _helper = new LuceneTestHelper(CreateIndex(), CreateDocuments());
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose() => _helper.Dispose();

    private static SearchIndex CreateIndex() => new()
    {
        Name = "dated",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
            new SearchField { Name = "Updated", Type = "Edm.DateTimeOffset", Filterable = true },
        ]
    };

    private static List<Document> CreateDocuments() =>
    [
        Doc("1", "2024-01-01T00:00:00Z"),
        Doc("2", Boundary),
        Doc("3", "2025-01-01T00:00:00Z"),
        Doc("4", null),
    ];

    private static Document Doc(string id, string? updated)
    {
        var json = new JsonObject { ["Id"] = id };

        if (updated != null)
        {
            json["Updated"] = DateTimeOffset.Parse(updated);
        }

        var doc = new Document();

        foreach (var field in CreateIndex().Fields)
        {
            if (json[field.Name] is not { } value)
            {
                continue;
            }

            foreach (var luceneField in field.CreateFields(value))
            {
                doc.Add(luceneField);
            }
        }

        return doc;
    }

    private List<string> Search(string filter)
    {
        var query = new UriQueryExpressionParser(100)
            .ParseFilter(filter)
            .Accept(new ODataQueryVisitor(_helper.Index));

        return _searcher.Search(query, 100).ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData($"Updated gt {Boundary}", new[] { "3" })]
    [InlineData($"Updated ge {Boundary}", new[] { "2", "3" })]
    [InlineData($"Updated lt {Boundary}", new[] { "1" })]
    [InlineData($"Updated le {Boundary}", new[] { "1", "2" })]
    public void RangeOnDateField_MatchesDocumentsOnTheRightSideOfTheBoundary(string filter, string[] expected)
    {
        Assert.Equal(expected, Search(filter));
    }

    /// <summary>
    /// The same instant written with a different offset must give the same result, since the
    /// comparison is between instants, not between the written forms.
    /// </summary>
    [Fact]
    public void RangeOnDateField_ComparesInstantsWhateverTheOffset()
    {
        Assert.Equal(["2", "3"], Search("Updated ge 2024-06-01T02:00:00+02:00"));
    }

    [Fact]
    public void ClosedDateRange_MatchesDocumentsBetweenTheBounds()
    {
        Assert.Equal(["2"], Search("Updated gt 2024-01-01T00:00:00Z and Updated lt 2025-01-01T00:00:00Z"));
    }
}
