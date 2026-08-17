using System.Globalization;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for faceted search (issue #43).
/// </summary>
/// <remarks>
/// A facet expression is a field path plus comma-separated options —
/// <c>Category,count:5,sort:-value</c> — and produces either one bucket per distinct value or,
/// with <c>values</c>/<c>interval</c>, one bucket per range. See
/// https://learn.microsoft.com/en-us/azure/search/search-faceted-navigation.
///
/// The counts are over the whole match set rather than the returned page, so these tests
/// deliberately assert counts larger than <c>Top</c> in places: that a facet survives paging
/// is the property that makes it useful for navigation.
/// </remarks>
public class FacetTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public FacetTests()
    {
        var index = CreateIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments(index));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    /// <summary>
    /// A hotel index covering each facetable shape: a plain string, a string that is also
    /// searchable (so its analyzed tokens could be mistaken for facet values), a hidden
    /// string, numerics, a boolean, a date, a string collection, and a sub-field of both a
    /// complex field and a complex collection.
    /// </summary>
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
            new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
            new SearchField { Name = "Category", Type = "Edm.String", Facetable = true },
            // Searchable AND facetable: the analyzed copy must not leak into the buckets.
            new SearchField { Name = "Slogan", Type = "Edm.String", Searchable = true, Facetable = true },
            // Facetable but hidden, which stored fields alone could not serve.
            new SearchField { Name = "Secret", Type = "Edm.String", Facetable = true, Retrievable = false },
            new SearchField { Name = "Rating", Type = "Edm.Int32", Facetable = true, Filterable = true },
            new SearchField { Name = "BaseRate", Type = "Edm.Double", Facetable = true },
            new SearchField { Name = "Renovated", Type = "Edm.Boolean", Facetable = true },
            new SearchField { Name = "LastRenovationDate", Type = "Edm.DateTimeOffset", Facetable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Facetable = true },
            // Marked facetable so that the type-specific rejections below are what gets
            // exercised, rather than the earlier "not facetable" check.
            new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true, Facetable = true },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Facetable = true,
                Fields =
                {
                    new SearchField { Name = "City", Type = "Edm.String", Facetable = true },
                    new SearchField { Name = "StateProvince", Type = "Edm.String", Facetable = true },
                }
            },
            new SearchField
            {
                Name = "Rooms",
                Type = ComplexTypeSupport.ComplexCollectionType,
                Fields =
                {
                    new SearchField { Name = "Type", Type = "Edm.String", Facetable = true },
                    new SearchField { Name = "BaseRate", Type = "Edm.Double", Facetable = true },
                }
            },
        ]
    };

    private static List<Lucene.Net.Documents.Document> CreateDocuments(SearchIndex index)
    {
        var rows = new List<JsonObject>
        {
            new()
            {
                ["Id"] = "1",
                ["Name"] = "Seattle Downtown",
                ["Category"] = "Resort and Spa",
                ["Slogan"] = "Resort and Spa",
                ["Secret"] = "alpha",
                ["Rating"] = 5,
                ["BaseRate"] = 250.0,
                ["Renovated"] = true,
                ["LastRenovationDate"] = JsonValue.Create(DateTimeOffset.Parse("2020-03-15T00:00:00Z", CultureInfo.InvariantCulture)),
                ["Tags"] = new JsonArray("wifi", "pool"),
                ["Address"] = new JsonObject { ["City"] = "Seattle", ["StateProvince"] = "WA" },
                // Two deluxe rooms: the hotel must still count once toward "Deluxe".
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Deluxe", ["BaseRate"] = 250.0 },
                    new JsonObject { ["Type"] = "Deluxe", ["BaseRate"] = 260.0 }),
            },
            new()
            {
                ["Id"] = "2",
                ["Name"] = "Bellevue Suites",
                ["Category"] = "Resort and Spa",
                ["Slogan"] = "Resort and Spa",
                ["Secret"] = "alpha",
                ["Rating"] = 4,
                ["BaseRate"] = 180.0,
                ["Renovated"] = true,
                ["LastRenovationDate"] = JsonValue.Create(DateTimeOffset.Parse("2021-07-01T00:00:00Z", CultureInfo.InvariantCulture)),
                ["Tags"] = new JsonArray("wifi", "kitchen"),
                ["Address"] = new JsonObject { ["City"] = "Bellevue", ["StateProvince"] = "WA" },
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Suite", ["BaseRate"] = 400.0 }),
            },
            new()
            {
                ["Id"] = "3",
                ["Name"] = "Portland Budget",
                ["Category"] = "Budget",
                ["Slogan"] = "Cheap and cheerful",
                ["Secret"] = "beta",
                ["Rating"] = 3,
                ["BaseRate"] = 95.0,
                ["Renovated"] = false,
                ["LastRenovationDate"] = JsonValue.Create(DateTimeOffset.Parse("2015-01-20T00:00:00Z", CultureInfo.InvariantCulture)),
                ["Tags"] = new JsonArray("wifi"),
                ["Address"] = new JsonObject { ["City"] = "Portland", ["StateProvince"] = "OR" },
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Standard", ["BaseRate"] = 95.0 }),
            },
            new()
            {
                ["Id"] = "4",
                ["Name"] = "Tacoma Motel",
                ["Category"] = "Budget",
                ["Slogan"] = "Cheap and cheerful",
                ["Secret"] = "beta",
                ["Rating"] = 3,
                ["BaseRate"] = 60.0,
                ["Renovated"] = false,
                ["LastRenovationDate"] = JsonValue.Create(DateTimeOffset.Parse("2015-06-10T00:00:00Z", CultureInfo.InvariantCulture)),
                ["Tags"] = new JsonArray("parking"),
                ["Address"] = new JsonObject { ["City"] = "Tacoma", ["StateProvince"] = "WA" },
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Standard", ["BaseRate"] = 60.0 }),
            },
        };

        return rows.Select(row => BuildDocument(index, row)).ToList();
    }

    private static Lucene.Net.Documents.Document BuildDocument(SearchIndex index, JsonObject row)
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
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<FacetBucket>>> Facet(
        params string[] facets)
        => await Facet(null, 50, facets);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<FacetBucket>>> Facet(
        string? filter,
        int top,
        params string[] facets)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = filter,
            Facets = facets,
            Top = top,
        });

        Assert.NotNull(response.Facets);
        return response.Facets;
    }

    private static (object? Value, int Count)[] Pairs(IReadOnlyList<FacetBucket> buckets)
        => buckets.Select(b => (b.Value, b.Count)).ToArray();

    // ===== Value facets =====

    [Fact]
    public async Task Facet_OnString_CountsEachDistinctValue()
    {
        var facets = await Facet("Category");

        // Both categories have two hotels, and the default sort breaks a tie on count by
        // value ascending, so Budget precedes Resort and Spa.
        Assert.Equal(
            [("Budget", 2), ("Resort and Spa", 2)],
            Pairs(facets["Category"]));
    }

    [Fact]
    public async Task Facet_OnSearchableField_UsesRawValueNotAnalyzedTokens()
    {
        // The searchable copy of this field is analyzed into "resort"/"spa"/"cheap"/... .
        // Those tokens are not facet values and must not appear as buckets.
        var facets = await Facet("Slogan");

        Assert.Equal(
            [("Cheap and cheerful", 2), ("Resort and Spa", 2)],
            Pairs(facets["Slogan"]));
    }

    [Fact]
    public async Task Facet_OnHiddenField_StillCounts()
    {
        // Facetable but not retrievable, so its values are never in the stored document.
        var facets = await Facet("Secret");

        Assert.Equal([("alpha", 2), ("beta", 2)], Pairs(facets["Secret"]));
    }

    [Fact]
    public async Task Facet_OnCollection_CountsEachElement()
    {
        var facets = await Facet("Tags");

        Assert.Equal(
            [("wifi", 3), ("kitchen", 1), ("parking", 1), ("pool", 1)],
            Pairs(facets["Tags"]));
    }

    [Fact]
    public async Task Facet_OnComplexSubField_CountsByPath()
    {
        var facets = await Facet("Address/StateProvince");

        Assert.Equal([("WA", 3), ("OR", 1)], Pairs(facets["Address/StateProvince"]));
    }

    [Fact]
    public async Task Facet_OnComplexCollectionSubField_CountsParentDocumentOnce()
    {
        // Hotel 1 has two Deluxe rooms. Azure Search counts the parent document, so Deluxe
        // must be 1 rather than 2.
        var facets = await Facet("Rooms/Type");

        Assert.Equal(
            [("Deluxe", 1), ("Standard", 2), ("Suite", 1)],
            Pairs(facets["Rooms/Type"]).OrderBy(p => (string)p.Value!).ToArray());
    }

    [Fact]
    public async Task Facet_OnNumeric_CountsDistinctValues()
    {
        var facets = await Facet("Rating");

        Assert.Equal([(3, 2), (4, 1), (5, 1)], Pairs(facets["Rating"]));
    }

    [Fact]
    public async Task Facet_OnBoolean_CountsTrueAndFalse()
    {
        var facets = await Facet("Renovated");

        Assert.Equal([(false, 2), (true, 2)], Pairs(facets["Renovated"]));
    }

    // ===== count and sort =====

    [Fact]
    public async Task Facet_Count_LimitsBuckets()
    {
        var facets = await Facet("Tags,count:2");

        Assert.Equal([("wifi", 3), ("kitchen", 1)], Pairs(facets["Tags"]));
    }

    [Fact]
    public async Task Facet_CountZero_ReturnsAllBuckets()
    {
        // count:0 means "no limit" in Azure Search, not "no buckets".
        var facets = await Facet("Tags,count:0");

        Assert.Equal(4, facets["Tags"].Count);
    }

    [Fact]
    public async Task Facet_DefaultsToTenBuckets()
    {
        var facets = await Facet("Tags");

        // Fewer than ten distinct tags exist, so the default cap does not bite here; the
        // point is that it does not truncate below the real count either.
        Assert.Equal(4, facets["Tags"].Count);
    }

    [Fact]
    public async Task Facet_SortByCountAscending_OrdersSmallestFirst()
    {
        var facets = await Facet("Tags,sort:-count");

        Assert.Equal(
            [("kitchen", 1), ("parking", 1), ("pool", 1), ("wifi", 3)],
            Pairs(facets["Tags"]));
    }

    [Fact]
    public async Task Facet_SortByValue_OrdersAlphabetically()
    {
        var facets = await Facet("Tags,sort:value");

        Assert.Equal(
            [("kitchen", 1), ("parking", 1), ("pool", 1), ("wifi", 3)],
            Pairs(facets["Tags"]));
    }

    [Fact]
    public async Task Facet_SortByValueDescending_OrdersReverseAlphabetically()
    {
        var facets = await Facet("Tags,sort:-value");

        Assert.Equal(
            [("wifi", 3), ("pool", 1), ("parking", 1), ("kitchen", 1)],
            Pairs(facets["Tags"]));
    }

    [Fact]
    public async Task Facet_SortByValue_OnNumeric_OrdersNumericallyNotLexically()
    {
        // A lexical ordering of the encoded values would put 10 before 3.
        var facets = await Facet("BaseRate,sort:value");

        Assert.Equal(
            [(60.0, 1), (95.0, 1), (180.0, 1), (250.0, 1)],
            Pairs(facets["BaseRate"]));
    }

    [Fact]
    public async Task Facet_CountAndSort_CombineInOneExpression()
    {
        var facets = await Facet("Tags,count:2,sort:value");

        Assert.Equal([("kitchen", 1), ("parking", 1)], Pairs(facets["Tags"]));
    }

    // ===== Range facets: values =====

    [Fact]
    public async Task Facet_Values_ProducesOneMoreBucketThanBounds()
    {
        var buckets = (await Facet("BaseRate,values:100|200"))["BaseRate"];

        Assert.Equal(3, buckets.Count);

        // The first bucket is open below and the last open above, so each carries only the
        // bound it has.
        Assert.Null(buckets[0].From);
        Assert.Equal(100.0, buckets[0].To);
        Assert.Equal(2, buckets[0].Count); // 60, 95

        Assert.Equal(100.0, buckets[1].From);
        Assert.Equal(200.0, buckets[1].To);
        Assert.Equal(1, buckets[1].Count); // 180

        Assert.Equal(200.0, buckets[2].From);
        Assert.Null(buckets[2].To);
        Assert.Equal(1, buckets[2].Count); // 250
    }

    [Fact]
    public async Task Facet_Values_KeepsEmptyBuckets()
    {
        // A range facet describes a fixed scale, so a bucket nothing falls into is still
        // reported — unlike a value facet, where an unseen value has no bucket at all.
        var buckets = (await Facet("BaseRate,values:1|2"))["BaseRate"];

        Assert.Equal(3, buckets.Count);
        Assert.Equal(0, buckets[0].Count);
        Assert.Equal(0, buckets[1].Count);
        Assert.Equal(4, buckets[2].Count);
    }

    [Fact]
    public async Task Facet_Values_BucketIsLowerInclusiveUpperExclusive()
    {
        // A bound of exactly 95 puts the 95.0 rate in the upper bucket, not the lower.
        var buckets = (await Facet("BaseRate,values:95"))["BaseRate"];

        Assert.Equal(1, buckets[0].Count); // 60
        Assert.Equal(3, buckets[1].Count); // 95, 180, 250
    }

    [Fact]
    public async Task Facet_Values_CountsParentDocumentOncePerBucket()
    {
        // Hotel 1's two rooms are 250 and 260, which are different values that fall in the
        // same bucket. The hotel must count once there, not once per room: Azure Search
        // counts parent documents rather than the sub-documents inside them.
        var buckets = (await Facet("Rooms/BaseRate,values:200"))["Rooms/BaseRate"];

        Assert.Equal(2, buckets.Count);

        // Under 200: hotels 3 and 4 (95 and 60).
        Assert.Equal(2, buckets[0].Count);

        // 200 and over: hotel 1 (250 and 260, counted once) and hotel 2 (400).
        Assert.Equal(2, buckets[1].Count);
    }

    [Fact]
    public async Task Facet_Values_OnDate_BucketsByTimestamp()
    {
        var buckets = (await Facet("LastRenovationDate,values:2018-01-01T00:00:00Z"))["LastRenovationDate"];

        Assert.Equal(2, buckets.Count);
        Assert.Equal(2, buckets[0].Count); // both 2015 renovations
        Assert.Equal(2, buckets[1].Count); // 2020 and 2021
    }

    // ===== Range facets: interval =====

    [Fact]
    public async Task Facet_NumericInterval_BucketsByWidth()
    {
        var buckets = (await Facet("BaseRate,interval:100"))["BaseRate"];

        // Rates are 60, 95, 180, 250, so buckets run to 100, 200, 300.
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(100.0, buckets[0].To);

        Assert.Equal(1, buckets[1].Count);
        Assert.Equal(1, buckets[2].Count);
    }

    [Fact]
    public async Task Facet_DateInterval_BucketsByYear()
    {
        var buckets = (await Facet("LastRenovationDate,interval:year"))["LastRenovationDate"];

        // 2015 (two hotels), then empty years, then 2020 and 2021.
        Assert.Equal(2, buckets[0].Count);
        Assert.Equal(4, buckets.Sum(b => b.Count));
    }

    // ===== Interaction with filter and paging =====

    [Fact]
    public async Task Facet_CountsWholeMatchSet_NotJustThePage()
    {
        // Top of 1 returns one document, but the facet still counts all four.
        var facets = await Facet(null, 1, "Category");

        Assert.Equal(4, facets["Category"].Sum(b => b.Count));
    }

    [Fact]
    public async Task Facet_IsNarrowedByFilter()
    {
        var facets = await Facet("Rating eq 3", 50, "Category");

        Assert.Equal([("Budget", 2)], Pairs(facets["Category"]));
    }

    [Fact]
    public async Task Facet_WithFilterMatchingNothing_ReturnsEmptyValueFacet()
    {
        var facets = await Facet("Rating eq 99", 50, "Category");

        Assert.Empty(facets["Category"]);
    }

    [Fact]
    public async Task Facet_WithFilterMatchingNothing_KeepsRangeBucketScale()
    {
        // A range facet's bounds come from the caller, not from the data, so the scale is
        // still reported when nothing matches — every bucket simply sits at zero.
        var facets = await Facet("Rating eq 99", 50, "BaseRate,values:100|200");

        var buckets = facets["BaseRate"];

        Assert.Equal(3, buckets.Count);
        Assert.All(buckets, b => Assert.Equal(0, b.Count));
        Assert.Equal(100.0, buckets[0].To);
    }

    [Fact]
    public async Task Facet_IntervalWithFilterMatchingNothing_ReturnsNoBuckets()
    {
        // An interval's bounds are derived from the matched values, so with nothing matched
        // there is no scale to report — unlike a values facet, whose bounds are given.
        var facets = await Facet("Rating eq 99", 50, "BaseRate,interval:100");

        Assert.Empty(facets["BaseRate"]);
    }

    [Fact]
    public async Task Facet_MultipleFacetsInOneRequest()
    {
        var facets = await Facet("Category", "Rating");

        Assert.Equal(2, facets.Count);
        Assert.Equal(2, facets["Category"].Count);
        Assert.Equal(3, facets["Rating"].Count);
    }

    [Fact]
    public async Task Search_WithoutFacets_ReturnsNoFacetStructure()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Top = 50,
        });

        Assert.Null(response.Facets);
    }

    [Fact]
    public async Task Facet_UsesSchemaCasing_NotRequestCasing()
    {
        // The response keys a facet by the schema's own spelling of the path.
        var facets = await Facet("category");

        Assert.True(facets.ContainsKey("Category"));
    }

    // ===== Validation =====

    [Fact]
    public async Task Facet_OnUnknownField_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Nonexistent"));

        Assert.Contains("Nonexistent", ex.Message);
    }

    [Fact]
    public async Task Facet_OnNonFacetableField_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Name"));

        Assert.Contains("not facetable", ex.Message);
    }

    [Fact]
    public async Task Facet_OnGeographyField_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Location"));

        Assert.Contains("geography", ex.Message);
    }

    [Fact]
    public async Task Facet_OnComplexField_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Address"));

        Assert.Contains("complex", ex.Message);
    }

    [Fact]
    public async Task Facet_CombiningValuesAndInterval_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("BaseRate,values:100,interval:50"));

        Assert.Contains("cannot be combined", ex.Message);
    }

    [Fact]
    public async Task Facet_CombiningCountWithValues_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("BaseRate,count:5,values:100"));

        Assert.Contains("cannot be combined", ex.Message);
    }

    [Fact]
    public async Task Facet_TimeOffsetWithoutInterval_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("LastRenovationDate,timeoffset:-01:00"));

        Assert.Contains("timeoffset", ex.Message);
    }

    [Fact]
    public async Task Facet_IntervalOnStringField_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Category,interval:5"));

        Assert.Contains("numeric", ex.Message);
    }

    [Fact]
    public async Task Facet_UnorderedValues_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("BaseRate,values:200|100"));

        Assert.Contains("ascending", ex.Message);
    }

    [Fact]
    public async Task Facet_InvalidSort_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Tags,sort:sideways"));

        Assert.Contains("sort", ex.Message);
    }

    [Fact]
    public async Task Facet_UnknownOption_Throws()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Facet("Tags,nonsense:1"));

        Assert.Contains("nonsense", ex.Message);
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
