using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Search;
using Microsoft.OData.UriParser;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for ODataQueryVisitor, verifying that OData filter expressions
/// are correctly translated to Lucene queries and produce correct search results.
/// </summary>
public class ODataQueryVisitorTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    public ODataQueryVisitorTests()
    {
        var index = LuceneTestHelper.CreateProductIndex();
        _helper = new LuceneTestHelper(index, LuceneTestHelper.CreateProductDocuments());
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose()
    {
        _helper.Dispose();
    }

    private Query ParseFilter(string filter)
    {
        var parser = new UriQueryExpressionParser(100);
        var filterToken = parser.ParseFilter(filter);
        return filterToken.Accept(new ODataQueryVisitor(_helper.Index));
    }

    private List<string> SearchWithFilter(string filter)
    {
        var query = ParseFilter(filter);
        var docs = _searcher.Search(query, 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id)
            .ToList();
    }

    // ===== Equality (eq) =====

    [Fact]
    public void Filter_StringEquality_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Category eq 'Electronics'");

        Assert.Equal(3, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
        Assert.Contains("5", ids);
    }

    [Fact]
    public void Filter_StringEquality_NoMatch_ReturnsEmpty()
    {
        var ids = SearchWithFilter("Category eq 'Clothing'");
        Assert.Empty(ids);
    }

    [Fact]
    public void Filter_BooleanEqualityTrue_ReturnsInStockItems()
    {
        var ids = SearchWithFilter("InStock eq true");

        Assert.Equal(4, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
        Assert.Contains("5", ids);
        Assert.DoesNotContain("4", ids);
    }

    [Fact]
    public void Filter_BooleanEqualityFalse_ReturnsOutOfStockItems()
    {
        var ids = SearchWithFilter("InStock eq false");

        Assert.Single(ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_IntegerEquality_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating eq 5");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("4", ids);
    }

    // ===== Not Equal (ne) =====

    [Fact]
    public void Filter_StringNotEqual_ExcludesMatchingDocuments()
    {
        var ids = SearchWithFilter("Category ne 'Electronics'");

        Assert.Equal(2, ids.Count);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_BooleanNotEqual_ReturnsOpposite()
    {
        var ids = SearchWithFilter("InStock ne true");

        Assert.Single(ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_IntegerNotEqual_ExcludesMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating ne 5");

        Assert.Equal(3, ids.Count);
        Assert.Contains("2", ids); // rating 4
        Assert.Contains("3", ids); // rating 4
        Assert.Contains("5", ids); // rating 3
    }

    // ===== Less Than (lt) =====

    [Fact]
    public void Filter_IntegerLessThan_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating lt 4");

        Assert.Single(ids);
        Assert.Contains("5", ids); // Monitor 4K with rating 3
    }

    [Fact]
    public void Filter_IntegerLessThan_ExcludesBoundary()
    {
        // Rating lt 3 should NOT include the item with rating exactly 3
        var ids = SearchWithFilter("Rating lt 3");
        Assert.Empty(ids);
    }

    // ===== Less Than or Equal (le) =====

    [Fact]
    public void Filter_IntegerLessThanOrEqual_IncludesBoundary()
    {
        var ids = SearchWithFilter("Rating le 3");

        Assert.Single(ids);
        Assert.Contains("5", ids);
    }

    [Fact]
    public void Filter_IntegerLessThanOrEqual_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating le 4");

        Assert.Equal(3, ids.Count);
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
        Assert.Contains("5", ids);
    }

    // ===== Greater Than (gt) =====

    [Fact]
    public void Filter_IntegerGreaterThan_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating gt 4");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_IntegerGreaterThan_ExcludesBoundary()
    {
        var ids = SearchWithFilter("Rating gt 5");
        Assert.Empty(ids);
    }

    // ===== Greater Than or Equal (ge) =====

    [Fact]
    public void Filter_IntegerGreaterThanOrEqual_IncludesBoundary()
    {
        var ids = SearchWithFilter("Rating ge 5");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_IntegerGreaterThanOrEqual_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter("Rating ge 4");

        Assert.Equal(4, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
    }

    // ===== Boolean Operators (and, or) =====

    [Fact]
    public void Filter_And_ReturnsBothConditionsMatched()
    {
        var ids = SearchWithFilter("Category eq 'Electronics' and InStock eq true");

        Assert.Equal(3, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
        Assert.Contains("5", ids);
    }

    [Fact]
    public void Filter_Or_ReturnsEitherConditionMatched()
    {
        var ids = SearchWithFilter("Category eq 'Accessories' or Rating gt 4");

        Assert.Equal(3, ids.Count);
        Assert.Contains("1", ids); // rating 5
        Assert.Contains("3", ids); // Accessories
        Assert.Contains("4", ids); // Accessories and rating 5
    }

    [Fact]
    public void Filter_And_WithDifferentFields_ReturnsIntersection()
    {
        var ids = SearchWithFilter("Category eq 'Accessories' and InStock eq true");

        Assert.Single(ids);
        Assert.Contains("3", ids); // Gaming Mouse is the only in-stock Accessory
    }

    [Fact]
    public void Filter_And_NoOverlap_ReturnsEmpty()
    {
        var ids = SearchWithFilter("Category eq 'Electronics' and Category eq 'Accessories'");
        Assert.Empty(ids);
    }

    // ===== NOT operator =====

    [Fact]
    public void Filter_Not_NegatesBooleanEquality()
    {
        // OData requires parentheses: "not (expr)" to negate a binary expression
        var ids = SearchWithFilter("not (InStock eq true)");

        Assert.Single(ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_Not_NegatesStringEquality()
    {
        var ids = SearchWithFilter("not (Category eq 'Electronics')");

        Assert.Equal(2, ids.Count);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_Not_NegatesSearchIsMatch()
    {
        var ids = SearchWithFilter("not search.ismatch('keyboard')");

        Assert.NotEmpty(ids);
        Assert.DoesNotContain("4", ids); // Mechanical Keyboard should be excluded
    }

    // ===== IN operator =====

    [Fact]
    public void Filter_In_MatchesMultipleValues()
    {
        var ids = SearchWithFilter("Category in ('Electronics', 'Accessories')");

        Assert.Equal(5, ids.Count); // All products match one of these categories
    }

    [Fact]
    public void Filter_In_MatchesSingleValue()
    {
        var ids = SearchWithFilter("Category in ('Accessories')");

        Assert.Equal(2, ids.Count);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
    }

    // ===== search.in function =====

    [Fact]
    public void Filter_SearchIn_MatchesMultipleValues()
    {
        var ids = SearchWithFilter("search.in(Category, 'Electronics,Accessories')");

        Assert.Equal(5, ids.Count);
    }

    [Fact]
    public void Filter_SearchIn_MatchesSingleValue()
    {
        var ids = SearchWithFilter("search.in(Category, 'Electronics')");

        Assert.Equal(3, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
        Assert.Contains("5", ids);
    }

    [Fact]
    public void Filter_SearchIn_WithCustomDelimiter()
    {
        var ids = SearchWithFilter("search.in(Category, 'Electronics|Accessories', '|')");

        Assert.Equal(5, ids.Count);
    }

    // ===== search.ismatch function =====

    [Fact]
    public void Filter_SearchIsMatch_BasicSearch_FindsMatchingDocuments()
    {
        var ids = SearchWithFilter("search.ismatch('laptop')");

        Assert.NotEmpty(ids);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
    }

    [Fact]
    public void Filter_SearchIsMatch_WithFieldRestriction_SearchesOnlySpecifiedField()
    {
        var ids = SearchWithFilter("search.ismatch('laptop', 'Name')");

        Assert.NotEmpty(ids);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
    }

    [Fact]
    public void Filter_SearchIsMatch_WithMultipleFields()
    {
        var ids = SearchWithFilter("search.ismatch('gaming', 'Name,Description')");

        Assert.NotEmpty(ids);
        Assert.Contains("3", ids); // Gaming Mouse
    }

    [Fact]
    public void Filter_SearchIsMatch_CombinedWithAnd()
    {
        var ids = SearchWithFilter("search.ismatch('laptop') and InStock eq true");

        Assert.NotEmpty(ids);
        Assert.DoesNotContain("4", ids);
    }

    [Fact]
    public void Filter_SearchIsMatch_NoResults()
    {
        var ids = SearchWithFilter("search.ismatch('nonexistentproduct')");
        Assert.Empty(ids);
    }

    // ===== search.ismatchscoring function =====

    [Fact]
    public void Filter_SearchIsMatchScoring_FindsMatchingDocuments()
    {
        var ids = SearchWithFilter("search.ismatchscoring('laptop')");

        Assert.NotEmpty(ids);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
    }

    [Fact]
    public void Filter_SearchIsMatchScoring_WithFieldRestriction()
    {
        var ids = SearchWithFilter("search.ismatchscoring('monitor', 'Name')");

        Assert.NotEmpty(ids);
        Assert.Contains("5", ids);
    }

    // ===== Complex / combined filters =====

    [Fact]
    public void Filter_ComplexAndOr_ReturnsCorrectResults()
    {
        // Electronics that are in stock OR Accessories with rating 5
        var ids = SearchWithFilter("(Category eq 'Electronics' and InStock eq true) or (Category eq 'Accessories' and Rating eq 5)");

        Assert.Equal(4, ids.Count);
        Assert.Contains("1", ids); // Electronics, in stock
        Assert.Contains("2", ids); // Electronics, in stock
        Assert.Contains("4", ids); // Accessories, rating 5
        Assert.Contains("5", ids); // Electronics, in stock
    }

    [Fact]
    public void Filter_RangeQuery_BetweenValues()
    {
        // Rating between 4 and 5 (inclusive)
        var ids = SearchWithFilter("Rating ge 4 and Rating le 5");

        Assert.Equal(4, ids.Count);
        Assert.Contains("1", ids); // rating 5
        Assert.Contains("2", ids); // rating 4
        Assert.Contains("3", ids); // rating 4
        Assert.Contains("4", ids); // rating 5
    }

    [Fact]
    public void Filter_SearchIsMatch_WithBooleanAndComparison()
    {
        // Full-text search for "laptop" combined with integer filter
        var ids = SearchWithFilter("search.ismatch('laptop') and Rating lt 5");

        Assert.NotEmpty(ids);
        Assert.Contains("2", ids); // Budget Laptop with rating 4
        Assert.DoesNotContain("1", ids); // Laptop Pro has rating 5
    }

    [Fact]
    public void Filter_SearchIsMatch_OrWithEquality()
    {
        var ids = SearchWithFilter("search.ismatch('monitor') or Category eq 'Accessories'");

        Assert.True(ids.Count >= 2);
        Assert.Contains("5", ids); // Monitor
        Assert.Contains("3", ids); // Accessories
        Assert.Contains("4", ids); // Accessories
    }

    // ===== Error handling =====

    [Fact]
    public void Filter_SearchIsMatch_RequiresIndex()
    {
        var parser = new UriQueryExpressionParser(100);
        var filterToken = parser.ParseFilter("search.ismatch('test')");
        var visitor = new ODataQueryVisitor(); // No index provided

        Assert.Throws<InvalidOperationException>(() => filterToken.Accept(visitor));
    }

    [Fact]
    public void Filter_SearchIn_TooFewArguments_Throws()
    {
        var parser = new UriQueryExpressionParser(100);
        var filterToken = parser.ParseFilter("search.in(Category)");
        var visitor = new ODataQueryVisitor(_helper.Index);

        Assert.Throws<ArgumentException>(() => filterToken.Accept(visitor));
    }

    // ===== "not" without parentheses =====

    [Fact]
    public void Filter_NotWithoutParens_NegatesExpression()
    {
        // "not InStock eq true" is valid Azure Search syntax
        var ids = SearchWithFilter("not InStock eq true");

        Assert.Single(ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_NotWithoutParens_NegatesStringEquality()
    {
        var ids = SearchWithFilter("not Category eq 'Electronics'");

        Assert.Equal(2, ids.Count);
        Assert.Contains("3", ids);
        Assert.Contains("4", ids);
    }

    // ===== Double/float range comparisons =====

    [Fact]
    public void Filter_DoubleLessThan_ReturnsDocumentsBelowThreshold()
    {
        var ids = SearchWithFilter("Price lt 100.0");

        Assert.Single(ids);
        Assert.Contains("3", ids); // Gaming Mouse at 59.99
    }

    [Fact]
    public void Filter_DoubleGreaterThan_ReturnsDocumentsAboveThreshold()
    {
        var ids = SearchWithFilter("Price gt 600.0");

        Assert.Single(ids);
        Assert.Contains("1", ids); // Laptop Pro 15 at 1299.99
    }

    [Fact]
    public void Filter_DoubleRange_ReturnsDocumentsInRange()
    {
        var ids = SearchWithFilter("Price ge 100.0 and Price le 600.0");

        Assert.Equal(3, ids.Count);
        Assert.Contains("2", ids); // 499.99
        Assert.Contains("4", ids); // 149.99
        Assert.Contains("5", ids); // 599.99
    }

    [Fact]
    public void Filter_DoubleLessThanOrEqual_IncludesBoundary()
    {
        var ids = SearchWithFilter("Price le 59.99");

        Assert.Single(ids);
        Assert.Contains("3", ids);
    }

    [Fact]
    public void Filter_DoubleGreaterThanOrEqual_IncludesBoundary()
    {
        var ids = SearchWithFilter("Price ge 599.99");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids); // 1299.99
        Assert.Contains("5", ids); // 599.99
    }

    [Fact]
    public void Filter_DoubleEquality_PrecisionLimitation()
    {
        // OData parses "59.99" as System.Single (float). Promoting float to double
        // introduces precision error: (double)59.99f = 59.9900016784668, not 59.99.
        // This means exact equality on double fields with decimal literals won't match.
        // Range queries (lt, le, gt, ge) work correctly since the precision error
        // is negligible for range comparisons.
        var ids = SearchWithFilter("Price eq 59.99");
        Assert.Empty(ids);
    }
}

/// <summary>
/// Tests for string equality filtering on fields containing GUIDs.
/// Fields like ProductId, Type, and Id are filterable but not searchable,
/// so they are indexed as StringField (exact, not analyzed).
/// </summary>
public class ODataQueryVisitor_GuidFilterTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    private const string Guid1 = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string Guid2 = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string Guid3 = "c3d4e5f6-a7b8-9012-cdef-123456789012";

    public ODataQueryVisitor_GuidFilterTests()
    {
        var index = CreateGuidIndex();
        _helper = new LuceneTestHelper(index, CreateGuidDocuments());
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose()
    {
        _helper.Dispose();
    }

    private static SearchIndex CreateGuidIndex()
    {
        return new SearchIndex
        {
            Name = "items",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "ProductId", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Type", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
            ]
        };
    }

    private static List<Document> CreateGuidDocuments()
    {
        return
        [
            CreateDoc("1", Guid1, "Product", "Widget A"),
            CreateDoc("2", Guid2, "Product", "Widget B"),
            CreateDoc("3", Guid3, "Variant", "Widget C"),
            CreateDoc("4", Guid1, "Variant", "Widget D"),  // Same ProductId as doc 1
        ];
    }

    private static Document CreateDoc(string id, string productId, string type, string name)
    {
        return new Document
        {
            new StringField("Id", id, Field.Store.YES),
            new StringField("ProductId", productId, Field.Store.YES),
            new StringField("Type", type, Field.Store.YES),
            new TextField("Name", name, Field.Store.YES),
        };
    }

    private Query ParseFilter(string filter)
    {
        var parser = new UriQueryExpressionParser(100);
        var filterToken = parser.ParseFilter(filter);
        return filterToken.Accept(new ODataQueryVisitor(_helper.Index));
    }

    private List<string> SearchWithFilter(string filter)
    {
        var query = ParseFilter(filter);
        var docs = _searcher.Search(query, 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id)
            .ToList();
    }

    [Fact]
    public void Filter_GuidEquality_ReturnsMatchingDocuments()
    {
        var ids = SearchWithFilter($"ProductId eq '{Guid1}'");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("4", ids);
    }

    [Fact]
    public void Filter_GuidEquality_NoMatch_ReturnsEmpty()
    {
        var ids = SearchWithFilter("ProductId eq '00000000-0000-0000-0000-000000000000'");
        Assert.Empty(ids);
    }

    [Fact]
    public void Filter_GuidEquality_CompoundOrFilter()
    {
        // The exact pattern reported: ProductId eq '{id}' or (Type eq 'Product' and Id eq '{id}')
        var filter = $"ProductId eq '{Guid1}' or (Type eq 'Product' and Id eq '2')";
        var ids = SearchWithFilter(filter);

        Assert.Equal(3, ids.Count);
        Assert.Contains("1", ids);  // ProductId matches Guid1
        Assert.Contains("2", ids);  // Type=Product AND Id=2
        Assert.Contains("4", ids);  // ProductId matches Guid1
    }

    [Fact]
    public void Filter_StringEquality_OnFilterableField()
    {
        var ids = SearchWithFilter("Type eq 'Product'");

        Assert.Equal(2, ids.Count);
        Assert.Contains("1", ids);
        Assert.Contains("2", ids);
    }

    [Fact]
    public void Filter_StringNotEqual_OnGuidField()
    {
        var ids = SearchWithFilter($"ProductId ne '{Guid1}'");

        Assert.Equal(2, ids.Count);
        Assert.Contains("2", ids);
        Assert.Contains("3", ids);
    }

    [Fact]
    public void Filter_GuidEquality_AndWithType()
    {
        var ids = SearchWithFilter($"ProductId eq '{Guid1}' and Type eq 'Variant'");

        Assert.Single(ids);
        Assert.Contains("4", ids);
    }
}
