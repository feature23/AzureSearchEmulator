using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Faceting tests built from the worked examples in Microsoft's own documentation (issue #43),
/// using the <c>hotels-sample</c> index those examples query.
/// </summary>
/// <remarks>
/// These complement <see cref="FacetTests"/>, which covers each option mechanically. The point
/// here is different: reproduce the queries Azure Search's docs publish, against a schema
/// shaped like the one they publish it for, and assert the response they document — the
/// facet expressions from
/// https://learn.microsoft.com/en-us/azure/search/search-faceted-navigation-examples, notably
/// <c>Category</c>, <c>Tags,count:5</c>, <c>Rating,values:1|2|3|4|5</c>,
/// <c>Address/City,count:5</c>, and <c>Rooms/BaseRate,values:80|150|220</c>.
///
/// The sample corpus here is a reduced stand-in for the real 50-hotel index — the documented
/// counts are for all 50 — so the assertions are on the structure the docs specify and on
/// counts recomputed for these documents, not on the docs' own totals.
/// </remarks>
public class FacetDocsExampleTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public FacetDocsExampleTests()
    {
        var index = CreateHotelsSampleIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments(index));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    /// <summary>
    /// The <c>hotels-sample</c> schema as the faceted-navigation article publishes it, with
    /// <c>facetable</c> set on the low-cardinality fields it recommends: Category, Tags, and
    /// Rating, plus the Address and Rooms sub-fields its examples facet on.
    /// </summary>
    private static SearchIndex CreateHotelsSampleIndex() => new()
    {
        Name = "hotels-sample",
        Fields =
        [
            new SearchField { Name = "HotelId", Type = "Edm.String", Key = true, Searchable = false, Sortable = false, Facetable = false },
            new SearchField { Name = "HotelName", Type = "Edm.String", Searchable = true, Facetable = false },
            new SearchField { Name = "Description", Type = "Edm.String", Searchable = true, Filterable = false, Sortable = false, Facetable = false },
            new SearchField { Name = "Category", Type = "Edm.String", Filterable = true, Facetable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true, Facetable = true },
            new SearchField { Name = "Rating", Type = "Edm.Double", Filterable = true, Facetable = true },
            new SearchField { Name = "ParkingIncluded", Type = "Edm.Boolean", Filterable = true, Facetable = true },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Fields =
                {
                    new SearchField { Name = "City", Type = "Edm.String", Filterable = true, Facetable = true },
                    new SearchField { Name = "StateProvince", Type = "Edm.String", Filterable = true, Facetable = true },
                }
            },
            new SearchField
            {
                Name = "Rooms",
                Type = ComplexTypeSupport.ComplexCollectionType,
                Fields =
                {
                    new SearchField { Name = "Type", Type = "Edm.String", Searchable = true, Facetable = true },
                    new SearchField { Name = "BaseRate", Type = "Edm.Double", Filterable = true, Facetable = true },
                    new SearchField { Name = "SleepsCount", Type = "Edm.Int32", Filterable = true, Facetable = true },
                }
            },
        ]
    };

    /// <summary>
    /// Hotels drawn from the sample index, including the two the hierarchy example matches on
    /// "ocean" — Ocean Water Resort &amp; Spa in Tampa FL and Windy Ocean Motel in Honolulu HI.
    /// </summary>
    private static List<Lucene.Net.Documents.Document> CreateDocuments(SearchIndex index)
    {
        var rows = new List<JsonObject>
        {
            Hotel("1", "Stay-Kay City Hotel", "Boutique", 3.6, "New York", "NY",
                ["pool", "air conditioning", "concierge"],
                [("Budget Room", 96.99, 2), ("Deluxe Room", 150.99, 2)]),

            Hotel("2", "Old Century Hotel", "Boutique", 3.6, "Sarasota", "FL",
                ["pool", "free wifi", "concierge"],
                [("Budget Room", 80.99, 2), ("Standard Room", 120.99, 4)]),

            Hotel("3", "Gastronomic Landscape Hotel", "Suite", 4.8, "Seattle", "WA",
                ["restaurant", "bar", "continental breakfast"],
                [("Suite", 250.99, 4), ("Deluxe Room", 180.99, 2)]),

            Hotel("4", "Sublime Palace Hotel", "Boutique", 4.6, "San Antonio", "TX",
                ["concierge", "view", "air conditioning"],
                [("Deluxe Room", 200.99, 2)]),

            Hotel("5", "Red Tide Hotel", "Budget", 2.5, "Tampa", "FL",
                ["pool", "free wifi"],
                [("Budget Room", 60.99, 2), ("Budget Room", 65.99, 2)]),

            // One of the two "ocean" hotels from the facet hierarchy example.
            Hotel("6", "Ocean Water Resort & Spa", "Resort and Spa", 4.5, "Tampa", "FL",
                ["view", "pool", "restaurant"],
                [("Suite", 300.99, 4), ("Standard Room", 140.99, 2)]),

            // The other "ocean" hotel.
            Hotel("7", "Windy Ocean Motel", "Budget", 3.2, "Honolulu", "HI",
                ["pool", "air conditioning", "bar"],
                [("Suite", 175.99, 4), ("Budget Room", 85.99, 2), ("Standard Room", 110.99, 2)]),

            Hotel("8", "Lakeside B & B", "Budget", 4.1, "Seattle", "WA",
                ["free wifi", "continental breakfast"],
                [("Standard Room", 100.99, 2)]),

            Hotel("9", "Twin Dome Hotel", "Suite", 4.9, "Sarasota", "FL",
                ["pool", "restaurant", "view"],
                [("Suite", 400.99, 4)]),

            Hotel("10", "Triple Landscape Hotel", "Luxury", 4.8, "Seattle", "WA",
                ["bar", "view", "concierge"],
                [("Deluxe Room", 230.99, 2), ("Suite", 350.99, 4)]),
        };

        return rows.Select(row => BuildDocument(index, row)).ToList();
    }

    private static JsonObject Hotel(
        string id,
        string name,
        string category,
        double rating,
        string city,
        string state,
        string[] tags,
        (string Type, double BaseRate, int SleepsCount)[] rooms) => new()
    {
        ["HotelId"] = id,
        ["HotelName"] = name,
        ["Description"] = $"{name} in {city}.",
        ["Category"] = category,
        ["Rating"] = rating,
        ["ParkingIncluded"] = rooms.Length > 1,
        ["Tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)).ToArray()),
        ["Address"] = new JsonObject { ["City"] = city, ["StateProvince"] = state },
        ["Rooms"] = new JsonArray(rooms
            .Select(r => (JsonNode)new JsonObject
            {
                ["Type"] = r.Type,
                ["BaseRate"] = r.BaseRate,
                ["SleepsCount"] = r.SleepsCount,
            })
            .ToArray()),
    };

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

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<FacetBucket>>> Search(
        string search,
        string? filter,
        int top,
        params string[] facets)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = search,
            Filter = filter,
            Facets = facets,
            Top = top,
        });

        Assert.NotNull(response.Facets);
        return response.Facets;
    }

    /// <summary>
    /// The article's opening example: an empty query scoped to the whole index, faceting on
    /// Category, where every hotel falls into exactly one category.
    /// </summary>
    [Fact]
    public async Task BasicExample_FacetOnCategory_CountsEveryDocumentOnce()
    {
        var facets = await Search("*", null, 50, "Category");

        var categories = facets["Category"];

        // The documented shape: one bucket per category, ordered by count descending, with a
        // tie broken by value ascending (Boutique before Budget, both at three).
        Assert.Equal(
            [("Boutique", 3), ("Budget", 3), ("Suite", 2), ("Luxury", 1), ("Resort and Spa", 1)],
            categories.Select(b => ((string)b.Value!, b.Count)).ToArray());

        // "every hotel in the index is represented in exactly one of these categories"
        Assert.Equal(10, categories.Sum(b => b.Count));
    }

    /// <summary>
    /// <c>"facets": [ "Category", "Tags,count:5", "Rating,values:1|2|3|4|5" ]</c> — three
    /// facets in one request, with a count override on one and a range override on another.
    /// </summary>
    [Fact]
    public async Task BasicExample_ThreeFacets_WithCountAndValuesOverrides()
    {
        var facets = await Search("*", null, 50, "Category", "Tags,count:5", "Rating,values:1|2|3|4|5");

        Assert.Equal(["Category", "Rating", "Tags"], facets.Keys.Order());

        // "reduces the number of tags under the Tags section to the top five"
        Assert.Equal(5, facets["Tags"].Count);

        // Five bounds produce six buckets, as in the documented Rating response.
        var rating = facets["Rating"];
        Assert.Equal(6, rating.Count);

        // The documented bucket shape: the first carries only "to", the last only "from",
        // and each one in between carries both.
        Assert.Null(rating[0].From);
        Assert.Equal(1.0, rating[0].To);

        Assert.Equal(1.0, rating[1].From);
        Assert.Equal(2.0, rating[1].To);

        Assert.Equal(5.0, rating[^1].From);
        Assert.Null(rating[^1].To);

        // Ratings here run 2.5 to 4.9, so the outermost buckets are empty and every hotel
        // lands in one of the middle ones — exactly the pattern the docs' response shows.
        Assert.Equal(0, rating[0].Count);
        Assert.Equal(0, rating[^1].Count);
        Assert.Equal(10, rating.Sum(b => b.Count));
    }

    /// <summary>
    /// <c>"facets": [ "Address/City,count:5" ]</c> — faceting on a sub-field of a complex
    /// field, limited to five buckets.
    /// </summary>
    [Fact]
    public async Task BasicExample_FacetOnComplexSubField_LimitedToFive()
    {
        var facets = await Search("*", null, 50, "Address/City,count:5");

        var cities = facets["Address/City"];

        Assert.True(cities.Count <= 5);

        // Seattle and Tampa have the most hotels here, and a tie on count is broken by value.
        Assert.Equal("Seattle", cities[0].Value);
        Assert.Equal(3, cities[0].Count);
    }

    /// <summary>
    /// <c>"facets": [ "Rooms/BaseRate,values:80|150|220" ]</c> — the docs note that facets
    /// count the parent document rather than the sub-documents inside it, so a hotel with
    /// several rooms in one band still counts once toward that band.
    /// </summary>
    [Fact]
    public async Task BasicExample_RangeFacetOnComplexCollectionSubField_CountsHotelsNotRooms()
    {
        var facets = await Search("*", null, 50, "Rooms/BaseRate,values:80|150|220");

        var rates = facets["Rooms/BaseRate"];

        Assert.Equal(4, rates.Count);

        Assert.Null(rates[0].From);
        Assert.Equal(80.0, rates[0].To);
        Assert.Equal(220.0, rates[^1].From);
        Assert.Null(rates[^1].To);

        // Hotel 5 has two rooms under 80 (60.99 and 65.99) but counts once in that bucket, so
        // the bucket holds one hotel rather than two rooms — the docs' rule that facets count
        // the parent document and not the sub-documents inside it.
        Assert.Equal(1, rates[0].Count);

        Assert.Equal([1, 5, 4, 4], rates.Select(b => b.Count).ToArray());

        // A hotel with rooms in several bands appears in each of them, so the buckets sum to
        // more than the ten hotels — the docs' "the same document can be represented in
        // multiple facets".
        Assert.True(rates.Sum(b => b.Count) > 10);
    }

    /// <summary>
    /// The second basic example: a filter narrows the result set, and the facets narrow with
    /// it, since facets are computed from the current results.
    /// </summary>
    [Fact]
    public async Task BasicExample_FilterNarrowsFacets()
    {
        var facets = await Search("*", "Category eq 'Budget'", 50, "Tags");

        // Only the three Budget hotels contribute now, so no bucket can exceed three, and
        // "concierge" — carried only by non-Budget hotels — drops out of the structure
        // entirely.
        Assert.Equal(
            [("free wifi", 2), ("pool", 2), ("air conditioning", 1), ("bar", 1), ("continental breakfast", 1)],
            facets["Tags"].Select(b => ((string)b.Value!, b.Count)).ToArray());

        Assert.DoesNotContain("concierge", facets["Tags"].Select(b => (string)b.Value!));
    }

    /// <summary>
    /// The distinct-values example: <c>"top": 0</c> with <c>"count": true</c> returns just the
    /// facet counts and no documents.
    /// </summary>
    [Fact]
    public async Task DistinctValuesExample_TopZero_ReturnsFacetsWithoutDocuments()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Count = true,
            Top = 0,
            Facets = ["Category", "Address/StateProvince"],
        });

        Assert.Empty(response.Results);
        Assert.Equal(10, response.Count);

        Assert.NotNull(response.Facets);

        // The facet structure is complete even though no documents came back.
        Assert.Equal(10, response.Facets["Category"].Sum(b => b.Count));
        Assert.Equal(
            [("FL", 4), ("WA", 3), ("HI", 1), ("NY", 1), ("TX", 1)],
            response.Facets["Address/StateProvince"].Select(b => ((string)b.Value!, b.Count)).ToArray());
    }

    /// <summary>
    /// The hierarchy example's underlying query, minus the preview hierarchy syntax: a search
    /// for "ocean" matches the two ocean hotels, and the Tags facet counts only those two.
    /// </summary>
    [Fact]
    public async Task HierarchyExample_SearchScopesFacetsToMatchingDocuments()
    {
        var facets = await Search("ocean", null, 50, "Tags", "Address/StateProvince");

        var tags = facets["Tags"];

        // "Both hotels have pools. For other tags, only one hotel provides the amenity."
        Assert.Equal(("pool", 2), ((string)tags[0].Value!, tags[0].Count));
        Assert.All(tags.Skip(1), b => Assert.Equal(1, b.Count));

        // The two matching hotels are in FL and HI, one each.
        Assert.Equal(
            [("FL", 1), ("HI", 1)],
            facets["Address/StateProvince"].Select(b => ((string)b.Value!, b.Count)).ToArray());
    }

    /// <summary>
    /// <c>"facet=Rating,sort:-value"</c> — the docs' example of ordering buckets by value
    /// descending "irrespective of how many documents match each rating".
    /// </summary>
    [Fact]
    public async Task SortExample_ByValueDescending_IgnoresCounts()
    {
        var facets = await Search("*", null, 50, "Rating,sort:-value,count:0");

        var ratings = facets["Rating"].Select(b => (double)b.Value!).ToArray();

        Assert.Equal(ratings.OrderByDescending(r => r).ToArray(), ratings);
    }

    /// <summary>
    /// <c>"facet=Category,count:3,sort:count"</c> — the docs' worked example of combining a
    /// bucket limit with a descending count sort.
    /// </summary>
    [Fact]
    public async Task SortExample_CountAndSortCombined_TakesTopThreeByCount()
    {
        var facets = await Search("*", null, 50, "Category,count:3,sort:count");

        var categories = facets["Category"];

        Assert.Equal(3, categories.Count);

        // Descending by count, so the largest categories survive the cut.
        Assert.Equal(categories.Select(b => b.Count).OrderByDescending(c => c).ToArray(),
            categories.Select(b => b.Count).ToArray());
        Assert.Equal(3, categories[0].Count);
    }

    /// <summary>
    /// <c>"facet=baseRate,interval:100"</c> — the docs' example of interval buckets over a
    /// numeric field, which are anchored on multiples of the interval.
    /// </summary>
    [Fact]
    public async Task IntervalExample_NumericIntervalBucketsAnchorOnMultiples()
    {
        var facets = await Search("*", null, 50, "Rooms/BaseRate,interval:100");

        var buckets = facets["Rooms/BaseRate"];

        // Rates run 60.99 to 400.99, so the boundaries fall on 100, 200, 300, 400, 500.
        Assert.Equal(100.0, buckets[0].To);
        Assert.Equal(100.0, buckets[1].From);
        Assert.Equal(200.0, buckets[1].To);

        Assert.All(buckets, b => Assert.True(b.Count >= 0));
        Assert.True(buckets.Sum(b => b.Count) > 0);
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
