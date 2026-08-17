using AzureSearchEmulator.Searching;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the three <c>$filter</c> gaps in issue #44: null comparison, lexicographic
/// string ranges, and <c>search.ismatch</c> not contributing to relevance scoring.
/// </summary>
public class FilterGapTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    public FilterGapTests()
    {
        _helper = new LuceneTestHelper(
            LuceneTestHelper.CreateNullableIndex(),
            LuceneTestHelper.CreateNullableDocuments());
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose()
    {
        _helper.Dispose();
    }

    private Query ParseFilter(string filter)
    {
        var parser = new UriQueryExpressionParser(100);
        return parser.ParseFilter(filter).Accept(new ODataQueryVisitor(_helper.Index));
    }

    private List<string> SearchWithFilter(string filter)
    {
        var docs = _searcher.Search(ParseFilter(filter), 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    // ===== 1. Null comparison =====

    [Fact]
    public void Filter_EqNull_MatchesDocumentsMissingTheField()
    {
        // Doc 2 omits Category; doc 3 sends it as an explicit JSON null.
        Assert.Equal(["2", "3"], SearchWithFilter("Category eq null"));
    }

    [Fact]
    public void Filter_NeNull_IsTheComplementOfEqNull()
    {
        Assert.Equal(["1", "4", "5"], SearchWithFilter("Category ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnNumericField()
    {
        Assert.Equal(["2", "3"], SearchWithFilter("Rating eq null"));
    }

    [Fact]
    public void Filter_NeNull_OnNumericField()
    {
        Assert.Equal(["1", "4", "5"], SearchWithFilter("Rating ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnSearchableField_DoesNotMatchOnAnalyzedTerms()
    {
        // Name is searchable, so it is indexed twice — analyzed and raw — under one field
        // name. Every document has one, so nothing is null regardless of that duplication.
        Assert.Empty(SearchWithFilter("Name eq null"));
        Assert.Equal(["1", "2", "3", "4", "5"], SearchWithFilter("Name ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnGeographyPointField()
    {
        // A geography point indexes nothing under its own field name, only the coordinate
        // sidecars, so a naive term-presence test would report every document as null here.
        Assert.Equal(["3", "4", "5"], SearchWithFilter("Location eq null"));
        Assert.Equal(["1", "2"], SearchWithFilter("Location ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnCollectionField_TreatsEmptyCollectionAsNull()
    {
        // Doc 3 has no Tags, doc 4 omits it, and doc 5 sends an empty array — none of which
        // index a value, which is how Azure Search treats an empty collection in a filter.
        Assert.Equal(["3", "4", "5"], SearchWithFilter("Tags eq null"));
        Assert.Equal(["1", "2"], SearchWithFilter("Tags ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnComplexSubField()
    {
        // Doc 5 supplies an Address whose sub-fields are all null.
        Assert.Equal(["3", "5"], SearchWithFilter("Address/City eq null"));
        Assert.Equal(["1", "2", "4"], SearchWithFilter("Address/City ne null"));
    }

    [Fact]
    public void Filter_EqNull_OnComplexFieldItself()
    {
        // A complex field is null when every one of its leaves is, so doc 5's all-null
        // Address counts as null alongside doc 3's absent one.
        Assert.Equal(["3", "5"], SearchWithFilter("Address eq null"));
        Assert.Equal(["1", "2", "4"], SearchWithFilter("Address ne null"));
    }

    [Fact]
    public void Filter_NullComparison_CombinesWithOtherClauses()
    {
        Assert.Equal(["3"], SearchWithFilter("Category eq null and Location eq null"));
        Assert.Equal(["1", "5"], SearchWithFilter("Category eq 'Electronics' and Rating ne null"));
    }

    [Fact]
    public void Filter_NotEqNull_NegatesTheNullTest()
    {
        // "not X eq null" parses as "(not X) eq null", which has its own dispatch path.
        Assert.Equal(["1", "4", "5"], SearchWithFilter("not (Category eq null)"));
    }

    // ===== 2. String range comparisons =====

    [Fact]
    public void Filter_StringGreaterThanOrEqual_ReturnsLexicographicRange()
    {
        // Ordinal ordering over Alpha, Bravo, Charlie, delta, Echo: every uppercase name
        // sorts before the lowercase 'delta'.
        Assert.Equal(["3", "4", "5"], SearchWithFilter("Name ge 'Charlie'"));
    }

    [Fact]
    public void Filter_StringGreaterThan_ExcludesTheBound()
    {
        Assert.Equal(["4", "5"], SearchWithFilter("Name gt 'Charlie'"));
    }

    [Fact]
    public void Filter_StringLessThan_ReturnsLexicographicRange()
    {
        Assert.Equal(["1", "2"], SearchWithFilter("Name lt 'Charlie'"));
    }

    [Fact]
    public void Filter_StringLessThanOrEqual_IncludesTheBound()
    {
        Assert.Equal(["1", "2", "3"], SearchWithFilter("Name le 'Charlie'"));
    }

    [Fact]
    public void Filter_StringRange_IsOrdinalNotCaseInsensitive()
    {
        // Ordinal ordering puts every uppercase letter before every lowercase one, so
        // lowercase 'delta' is the only name above 'a' — a case-insensitive comparison
        // would instead return all five.
        Assert.Equal(["4"], SearchWithFilter("Name gt 'a'"));
    }

    [Fact]
    public void Filter_StringRange_BoundedOnBothSides()
    {
        Assert.Equal(["2", "3"], SearchWithFilter("Name ge 'B' and Name lt 'D'"));
    }

    [Fact]
    public void Filter_StringRange_OnNonSearchableField()
    {
        Assert.Equal(["1", "5"], SearchWithFilter("Category ge 'E'"));
        Assert.Equal(["4"], SearchWithFilter("Category lt 'E'"));
    }

    [Fact]
    public void Filter_StringRange_ExcludesDocumentsWithNoValue()
    {
        // Docs 2 and 3 have no Category, and a range must not match a missing value.
        var ids = SearchWithFilter("Category ge 'A'");

        Assert.DoesNotContain("2", ids);
        Assert.DoesNotContain("3", ids);
    }

    [Fact]
    public void Filter_StringRange_MatchesEachDocumentOnce()
    {
        // Name is searchable, so its analyzed terms sit under the same field name as the raw
        // filter copy and a range can match one document through several terms. The hits must
        // still be one per document.
        var docs = _searcher.Search(ParseFilter("Name ge 'A'"), 100);

        Assert.Equal(docs.ScoreDocs.Length, docs.ScoreDocs.Select(sd => sd.Doc).Distinct().Count());
    }

    // ===== 3. search.ismatch vs search.ismatchscoring =====

    [Fact]
    public void SearchIsMatch_DoesNotAffectScoring()
    {
        // Every matching document gets the same constant score, so the filter cannot reorder
        // results by relevance.
        var docs = _searcher.Search(ParseFilter("search.ismatch('Alpha OR Bravo')"), 100);

        Assert.NotEmpty(docs.ScoreDocs);
        Assert.Single(docs.ScoreDocs.Select(sd => sd.Score).Distinct());
    }

    [Fact]
    public void SearchIsMatchScoring_VariesScoreByRelevance()
    {
        var docs = _searcher.Search(ParseFilter("search.ismatchscoring('Alpha OR Bravo')"), 100);

        Assert.NotEmpty(docs.ScoreDocs);
        // The scoring variant keeps the parsed query's scores rather than flattening them.
        Assert.IsNotType<ConstantScoreQuery>(ParseFilter("search.ismatchscoring('Alpha')"));
    }

    [Fact]
    public void SearchIsMatch_IsWrappedInConstantScore()
    {
        Assert.IsType<ConstantScoreQuery>(ParseFilter("search.ismatch('Alpha')"));
    }

    [Fact]
    public void SearchIsMatch_MatchesTheSameDocumentsAsScoring()
    {
        // Only the scoring differs; the two functions must select the same documents.
        Assert.Equal(
            SearchWithFilter("search.ismatchscoring('Alpha OR Bravo')"),
            SearchWithFilter("search.ismatch('Alpha OR Bravo')"));
    }
}
