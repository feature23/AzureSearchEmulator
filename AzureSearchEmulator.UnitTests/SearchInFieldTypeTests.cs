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
/// Covers <c>search.in</c> over fields that are not strings (issue #72).
/// </summary>
/// <remarks>
/// Every entry in a <c>search.in</c> list arrives as text, while Lucene indexes each numeric
/// width, booleans and dates under its own encoding. Building a plain term query from that
/// text therefore matched nothing and the filter silently excluded every document — no error,
/// just an empty result that reads like a legitimate "no matches".
///
/// Each test asserts against the equivalent <c>or</c> chain of <c>eq</c> comparisons rather
/// than against a hard-coded id list, since <c>search.in</c> is documented as shorthand for
/// exactly that and agreement between the two is the property that was broken.
/// </remarks>
public class SearchInFieldTypeTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    public SearchInFieldTypeTests()
    {
        _helper = new LuceneTestHelper(CreateIndex(), CreateDocuments());
        _searcher = _helper.CreateSearcher();
    }

    public void Dispose() => _helper.Dispose();

    private static SearchIndex CreateIndex() => new()
    {
        Name = "typed",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
            new SearchField { Name = "Category", Type = "Edm.String", Filterable = true },
            new SearchField { Name = "Rating", Type = "Edm.Int32", Filterable = true },
            new SearchField { Name = "Views", Type = "Edm.Int64", Filterable = true },
            new SearchField { Name = "Price", Type = "Edm.Double", Filterable = true },
            new SearchField { Name = "InStock", Type = "Edm.Boolean", Filterable = true },
            new SearchField { Name = "Updated", Type = "Edm.DateTimeOffset", Filterable = true },
            new SearchField { Name = "Sizes", Type = "Collection(Edm.Int32)", Filterable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Filterable = true },
            new SearchField { Name = "NotFilterable", Type = "Edm.Int32", Filterable = false },
        ]
    };

    private static List<Document> CreateDocuments() =>
    [
        Doc("1", "a", 4, 4_000_000_000L, 9.5, true, "2024-01-01T00:00:00Z", [1, 2], ["x"]),
        Doc("2", "b", 5, 5_000_000_000L, 19.5, false, "2024-06-01T00:00:00Z", [2, 3], ["y"]),
        Doc("3", "c", 3, 6_000_000_000L, 29.5, true, "2025-01-01T00:00:00Z", [3, 4], ["z"]),
    ];

    private static Document Doc(
        string id,
        string category,
        int rating,
        long views,
        double price,
        bool inStock,
        string updated,
        int[] sizes,
        string[] tags)
    {
        var index = CreateIndex();

        var json = new JsonObject
        {
            ["Id"] = id,
            ["Category"] = category,
            ["Rating"] = rating,
            ["Views"] = views,
            ["Price"] = price,
            ["InStock"] = inStock,
            ["Updated"] = DateTimeOffset.Parse(updated),
            ["Sizes"] = new JsonArray(sizes.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
            ["Tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
            ["NotFilterable"] = rating,
        };

        var doc = new Document();

        foreach (var field in index.Fields)
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

    private Query ParseFilter(string filter)
    {
        var parser = new UriQueryExpressionParser(100);
        return parser.ParseFilter(filter).Accept(new ODataQueryVisitor(_helper.Index));
    }

    private List<string> Search(string filter)
    {
        var docs = _searcher.Search(ParseFilter(filter), 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Asserts that search.in agrees with the or-chain it is shorthand for, and that neither
    /// is trivially empty — an assertion of "both return nothing" would have passed against
    /// the broken code.
    /// </summary>
    private void AssertAgreesWithOrChain(string searchIn, string orChain, int expectedCount)
    {
        var actual = Search(searchIn);

        Assert.Equal(Search(orChain), actual);
        Assert.Equal(expectedCount, actual.Count);
    }

    [Fact]
    public void SearchIn_Int32Field_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(Rating, '4,5')",
            "Rating eq 4 or Rating eq 5",
            2);
    }

    [Fact]
    public void SearchIn_Int64Field_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(Views, '4000000000,6000000000')",
            "Views eq 4000000000 or Views eq 6000000000",
            2);
    }

    [Fact]
    public void SearchIn_DoubleField_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(Price, '9.5,29.5')",
            "Price eq 9.5 or Price eq 29.5",
            2);
    }

    [Fact]
    public void SearchIn_BooleanField_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(InStock, 'true')",
            "InStock eq true",
            2);
    }

    [Fact]
    public void SearchIn_DateTimeOffsetField_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(Updated, '2024-01-01T00:00:00Z,2025-01-01T00:00:00Z')",
            "Updated eq 2024-01-01T00:00:00Z or Updated eq 2025-01-01T00:00:00Z",
            2);
    }

    /// <summary>
    /// A numeric collection is indexed one value per element, so membership has to be read
    /// against the element type rather than the declared Collection(...) type.
    /// </summary>
    [Fact]
    public void SearchIn_Int32CollectionField_MatchesAnyElement()
    {
        AssertAgreesWithOrChain(
            "search.in(Sizes, '1,4')",
            "Sizes/any(s: s eq 1) or Sizes/any(s: s eq 4)",
            2);
    }

    [Fact]
    public void SearchIn_StringField_StillMatches()
    {
        AssertAgreesWithOrChain(
            "search.in(Category, 'a,c')",
            "Category eq 'a' or Category eq 'c'",
            2);
    }

    [Fact]
    public void SearchIn_StringCollectionField_StillMatches()
    {
        AssertAgreesWithOrChain(
            "search.in(Tags, 'x,z')",
            "Tags/any(t: t eq 'x') or Tags/any(t: t eq 'z')",
            2);
    }

    /// <summary>
    /// Azure lets a list entry be single-quoted so it can contain the delimiter; a numeric
    /// field should read the quoted form the same as the bare one.
    /// </summary>
    [Fact]
    public void SearchIn_QuotedNumericValues_AreUnquotedBeforeParsing()
    {
        AssertAgreesWithOrChain(
            "search.in(Rating, '''4'',''5''')",
            "Rating eq 4 or Rating eq 5",
            2);
    }

    /// <summary>
    /// A value outside the field's domain matches nothing rather than erroring, which is how
    /// Azure behaves — but it must not poison the entries beside it.
    /// </summary>
    [Fact]
    public void SearchIn_UnparseableNumericValue_MatchesNothingWithoutAffectingOthers()
    {
        AssertAgreesWithOrChain(
            "search.in(Rating, 'notanumber,4')",
            "Rating eq 4",
            1);
    }

    [Fact]
    public void SearchIn_CustomDelimiterOnNumericField_MatchesSameDocumentsAsOrChain()
    {
        AssertAgreesWithOrChain(
            "search.in(Rating, '4|5', '|')",
            "Rating eq 4 or Rating eq 5",
            2);
    }

    [Fact]
    public void SearchIn_NoMatchingValues_ReturnsNoDocuments()
    {
        Assert.Empty(Search("search.in(Rating, '99,100')"));
    }

    /// <summary>
    /// search.in previously skipped the filterable check that every other comparison runs,
    /// so a non-filterable field silently returned nothing instead of reporting the mistake.
    /// </summary>
    [Fact]
    public void SearchIn_NonFilterableField_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ParseFilter("search.in(NotFilterable, '4')"));

        Assert.Contains("not filterable", ex.Message);
    }
}
