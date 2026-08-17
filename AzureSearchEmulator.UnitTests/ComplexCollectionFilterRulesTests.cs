using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests derived directly from the documented Azure Search rules for lambda expressions over
/// <c>Collection(Edm.ComplexType)</c>.
/// </summary>
/// <remarks>
/// The rules these encode come from Microsoft's own reference, not from inference about what
/// the emulator happens to do:
///
/// <list type="bullet">
/// <item>
/// Cheat sheet — for <c>Collection(Edm.ComplexType)</c>, both <c>any</c> and <c>all</c> allow
/// "everything except <c>search.ismatch</c> and <c>search.ismatchscoring</c>". Unlike
/// <c>Collection(Edm.String)</c>, there is no eq/ne restriction:
/// https://learn.microsoft.com/en-us/azure/search/search-query-odata-collection-operators#limitations
/// </item>
/// <item>
/// Correlation — criteria inside one lambda apply to the *same* element:
/// https://learn.microsoft.com/en-us/azure/search/search-query-understand-collection-filters#correlated-versus-uncorrelated-search
/// </item>
/// <item>
/// Free variables — a lambda body may only reference fields bound to its own range variable:
/// https://learn.microsoft.com/en-us/azure/search/search-query-troubleshoot-collection-filters#rules-for-filtering-complex-collections
/// </item>
/// </list>
/// </remarks>
public class ComplexCollectionFilterRulesTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public ComplexCollectionFilterRulesTests()
    {
        var index = CreateIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments(index));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    /// <summary>
    /// The hotels schema from the Azure Search complex-types documentation.
    /// </summary>
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "HotelId", Type = "Edm.String", Key = true, Filterable = true },
            new SearchField { Name = "HotelName", Type = "Edm.String", Searchable = true, Filterable = true },
            new SearchField
            {
                Name = "Rooms",
                Type = ComplexTypeSupport.ComplexCollectionType,
                Fields =
                {
                    new SearchField { Name = "Type", Type = "Edm.String", Searchable = true, Filterable = true },
                    new SearchField { Name = "BaseRate", Type = "Edm.Double", Filterable = true },
                    new SearchField { Name = "SmokingAllowed", Type = "Edm.Boolean", Filterable = true },
                    new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
                }
            },
        ]
    };

    /// <summary>
    /// Documents chosen so that correlated and uncorrelated evaluation give *different*
    /// answers, which is the only way a test can tell them apart.
    /// </summary>
    private static List<Lucene.Net.Documents.Document> CreateDocuments(SearchIndex index)
    {
        var rows = new List<JsonObject>
        {
            // The decisive document: it has a Deluxe room AND a cheap room, but no room that
            // is BOTH. Correlated evaluation excludes it; uncorrelated evaluation returns it.
            new()
            {
                ["HotelId"] = "split",
                ["HotelName"] = "Split Criteria Hotel",
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Deluxe Room",
                        ["BaseRate"] = 250.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray("view"),
                    },
                    new JsonObject
                    {
                        ["Type"] = "Standard Room",
                        ["BaseRate"] = 80.0,
                        ["SmokingAllowed"] = true,
                        ["Tags"] = new JsonArray("tv"),
                    }),
            },
            // A single room satisfying both criteria at once: matches either way.
            new()
            {
                ["HotelId"] = "match",
                ["HotelName"] = "Genuine Match Hotel",
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Deluxe Room",
                        ["BaseRate"] = 90.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray("tv", "view"),
                    }),
            },
            // Every room is non-smoking and under 100: the "all" cases should match this.
            new()
            {
                ["HotelId"] = "budget",
                ["HotelName"] = "Budget Hotel",
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Standard Room",
                        ["BaseRate"] = 70.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray("tv"),
                    },
                    new JsonObject
                    {
                        ["Type"] = "Standard Room",
                        ["BaseRate"] = 95.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray(),
                    }),
            },
            // No rooms at all: "all" holds vacuously, "any" never does.
            new()
            {
                ["HotelId"] = "empty",
                ["HotelName"] = "Empty Hotel",
                ["Rooms"] = new JsonArray(),
            },
        };

        return rows.Select(row =>
        {
            var doc = new Lucene.Net.Documents.Document();
            foreach (var field in index.Fields)
            {
                if (row[field.Name] is { } value)
                {
                    foreach (var f in field.CreateFields(value))
                    {
                        doc.Add(f);
                    }
                }
            }
            return doc;
        }).ToList();
    }

    private async Task<List<string>> SearchIds(string filter)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = filter,
            Top = 50
        });

        return response.Results
            .Select(r => r["HotelId"]!.GetValue<string>())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    // ===== Correlation =====
    // https://learn.microsoft.com/en-us/azure/search/search-query-understand-collection-filters#correlated-versus-uncorrelated-search

    [Fact]
    public async Task Any_WithTwoCriteria_CorrelatesThemToTheSameElement()
    {
        // The documented example, verbatim. Per the docs, this returns "hotels that have at
        // least one deluxe room with a rate less than 100" — the criteria apply to the same
        // room, so a hotel whose deluxe room is expensive and whose cheap room is standard
        // must NOT match.
        var ids = await SearchIds("Rooms/any(room: room/Type eq 'Deluxe Room' and room/BaseRate lt 100)");

        Assert.Equal(["match"], ids);
    }

    [Fact]
    public async Task Any_WithTwoCriteria_DoesNotMatchWhenCriteriaAreSatisfiedByDifferentElements()
    {
        // Stated explicitly in the docs: "If filtering was uncorrelated, the above filter
        // might return hotels where one room is deluxe and a different room has a base rate
        // less than 100. That wouldn't make sense."
        var ids = await SearchIds("Rooms/any(room: room/Type eq 'Deluxe Room' and room/BaseRate lt 100)");

        Assert.DoesNotContain("split", ids);
    }

    [Fact]
    public async Task Any_CorrelatesAcrossASubFieldAndANestedCollection()
    {
        // "split" has a room tagged 'tv' and a room typed 'Deluxe Room', but not one room
        // that is both.
        var ids = await SearchIds("Rooms/any(room: room/Type eq 'Deluxe Room' and room/Tags/any(t: t eq 'tv'))");

        Assert.Equal(["match"], ids);
    }

    [Fact]
    public async Task All_WithTwoCriteria_CorrelatesThemToTheSameElement()
    {
        // Every room must be both non-smoking and under 100. "budget" qualifies; "split" has
        // a smoking room and an expensive one; "match" has a single qualifying room.
        // "empty" holds vacuously.
        var ids = await SearchIds("Rooms/all(room: room/SmokingAllowed eq false and room/BaseRate lt 100)");

        Assert.Equal(["budget", "empty", "match"], ids);
    }

    // ===== eq is valid inside all() over a complex collection =====
    // The cheat sheet allows "everything" for Collection(Edm.ComplexType) in both any and
    // all. The eq/ne restriction applies to Collection(Edm.String), not to complex
    // collections.

    [Fact]
    public async Task All_WithEquality_IsSupportedOverAComplexCollection()
    {
        // Documented in the $filter reference as a valid filter:
        // "$filter=ParkingIncluded eq true and Rooms/all(room: room/SmokingAllowed eq false)"
        // https://learn.microsoft.com/en-us/azure/search/search-query-odata-filter#examples
        var ids = await SearchIds("Rooms/all(room: room/SmokingAllowed eq false)");

        Assert.Equal(["budget", "empty", "match"], ids);
    }

    [Fact]
    public async Task All_WithNegatedSubField_IsEquivalentToEqualityAgainstFalse()
    {
        // The $filter reference gives these two as equivalent forms of the same query:
        //   Rooms/all(room: not room/SmokingAllowed)
        //   Rooms/all(room: room/SmokingAllowed eq false)
        var negated = await SearchIds("Rooms/all(room: not room/SmokingAllowed)");
        var equality = await SearchIds("Rooms/all(room: room/SmokingAllowed eq false)");

        Assert.Equal(equality, negated);
    }

    [Fact]
    public async Task All_WithStringEquality_IsSupportedOverAComplexCollection()
    {
        // "budget" is the only hotel whose every room is a Standard Room; "empty" holds
        // vacuously.
        var ids = await SearchIds("Rooms/all(room: room/Type eq 'Standard Room')");

        Assert.Equal(["budget", "empty"], ids);
    }

    // ===== any() with no lambda tests for a non-empty collection =====
    // https://learn.microsoft.com/en-us/azure/search/search-query-odata-collection-operators#syntax

    [Fact]
    public async Task AnyWithNoLambda_MatchesDocumentsWithANonEmptyCollection()
    {
        var ids = await SearchIds("Rooms/any()");

        Assert.Equal(["budget", "match", "split"], ids);
    }

    [Fact]
    public async Task NotAnyWithNoLambda_MatchesDocumentsWithAnEmptyCollection()
    {
        // The documented way to find hotels with no rooms: "$filter=not Rooms/any()"
        var ids = await SearchIds("not Rooms/any()");

        Assert.Equal(["empty"], ids);
    }

    // ===== Documented restrictions =====

    [Fact]
    public async Task SearchIsMatch_InsideALambda_IsRejected()
    {
        // The only feature the cheat sheet excludes for complex collections. Azure returns:
        // "The function 'ismatch' has no parameters bound to the range variable ... Only
        // bound field references are supported inside lambda expressions."
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _searcher.Search(_helper.Index, new SearchRequest
            {
                Search = "*",
                Filter = "Rooms/any(room: search.ismatch('deluxe'))",
                Top = 50
            }));
    }

    [Fact]
    public async Task FreeVariable_InsideALambda_IsRejected()
    {
        // Per the troubleshooting doc, a lambda body may only reference fields bound to its
        // own range variable, so referring to the top-level HotelName inside the lambda is
        // invalid — it must be lifted out of the lambda instead.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _searcher.Search(_helper.Index, new SearchRequest
            {
                Search = "*",
                Filter = "Rooms/any(room: room/Type eq 'Deluxe Room' and HotelName ne 'Flagship')",
                Top = 50
            }));
    }

    [Fact]
    public async Task All_WithEquality_IsStillRejectedOverATopLevelStringCollection()
    {
        // The complement of All_WithEquality_IsSupportedOverAComplexCollection: the eq/ne
        // restriction is real for Collection(Edm.String), which the cheat sheet limits to
        // "comparisons with ne or not search.in()" inside all(...). A top-level string
        // collection's values are indexed as bare terms with nothing tying them to an
        // element, so "every value is 'x'" genuinely cannot be asked of the inverted index —
        // which is exactly why Azure allows it for complex collections and not for these.
        // https://learn.microsoft.com/en-us/azure/search/search-query-odata-collection-operators#limitations
        var index = new SearchIndex
        {
            Name = "products",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Filterable = true },
            ]
        };

        using var helper = new LuceneTestHelper(index, []);
        var searcher = new LuceneNetIndexSearcher(new StubReaderFactory(helper.Directory));

        await Assert.ThrowsAsync<NotImplementedException>(() =>
            searcher.Search(index, new SearchRequest
            {
                Search = "*",
                Filter = "Tags/all(t: t eq 'wifi')",
                Top = 50
            }));
    }

    [Fact]
    public async Task BoundVariableLiftedOutOfTheLambda_IsAllowed()
    {
        // The rewritten form the docs recommend for the free-variable case.
        var ids = await SearchIds(
            "Rooms/any(room: room/Type eq 'Deluxe Room') and HotelName ne 'Flagship'");

        Assert.Equal(["match", "split"], ids);
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
