using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Microsoft.OData;
using Microsoft.OData.UriParser;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for Edm.GeographyPoint fields and the geo.distance / geo.intersects OData
/// functions.
/// </summary>
/// <remarks>
/// The reference cities are at their real coordinates, so the distance thresholds used here
/// correspond to real distances from Seattle: Bellevue is ~9.8km, Tacoma ~40.2km,
/// Portland ~234km, and New York ~3866km.
/// </remarks>
public class GeospatialTests : IDisposable
{
    private const string Seattle = "geography'POINT(-122.3321 47.6062)'";

    private readonly LuceneTestHelper _helper;
    private readonly IndexSearcher _searcher;

    public GeospatialTests()
    {
        var index = LuceneTestHelper.CreateCityIndex();
        _helper = new LuceneTestHelper(index, LuceneTestHelper.CreateCityDocuments());
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
        var docs = _searcher.Search(ParseFilter(filter), 100);
        return docs.ScoreDocs
            .Select(sd => _searcher.Doc(sd.Doc).Get("Name"))
            .OrderBy(name => name)
            .ToList();
    }

    // ===== geo.distance in $filter =====

    [Fact]
    public void GeoDistance_LessThanOrEqual_ReturnsCitiesWithinRadius()
    {
        // Bellevue is ~9.8km away, so a 10km radius includes it but excludes Tacoma at ~40km.
        var names = SearchWithFilter($"geo.distance(Location, {Seattle}) le 10");

        Assert.Equal(["Bellevue", "Seattle"], names);
    }

    [Fact]
    public void GeoDistance_WiderRadius_IncludesFartherCities()
    {
        var names = SearchWithFilter($"geo.distance(Location, {Seattle}) le 50");

        Assert.Equal(["Bellevue", "Seattle", "Tacoma"], names);
    }

    [Fact]
    public void GeoDistance_GreaterThan_ReturnsCitiesOutsideRadius()
    {
        var names = SearchWithFilter($"geo.distance(Location, {Seattle}) gt 50");

        // Nowhere has no location, and a null point matches neither direction.
        Assert.Equal(["New York", "Portland"], names);
    }

    [Fact]
    public void GeoDistance_ArgumentsReversed_BehavesTheSame()
    {
        // Azure Search allows the constant and the field in either order.
        var names = SearchWithFilter($"geo.distance({Seattle}, Location) le 10");

        Assert.Equal(["Bellevue", "Seattle"], names);
    }

    [Fact]
    public void GeoDistance_ConstantOnLeftOfComparison_FlipsTheOperator()
    {
        // "10 ge geo.distance(...)" means the same as "geo.distance(...) le 10".
        var names = SearchWithFilter($"10 ge geo.distance(Location, {Seattle})");

        Assert.Equal(["Bellevue", "Seattle"], names);
    }

    [Fact]
    public void GeoDistance_ExclusiveComparison_ExcludesExactBoundary()
    {
        // Seattle is exactly 0km from itself, so "gt 0" must exclude it.
        var names = SearchWithFilter($"geo.distance(Location, {Seattle}) gt 0");

        Assert.DoesNotContain("Seattle", names);
        Assert.Contains("Bellevue", names);
    }

    [Fact]
    public void GeoDistance_NullLocation_NeverMatches()
    {
        // Azure Search evaluates geo.distance over a null field as null, which fails
        // every comparison, so Nowhere appears in neither result set.
        var within = SearchWithFilter($"geo.distance(Location, {Seattle}) le 100000");
        var beyond = SearchWithFilter($"geo.distance(Location, {Seattle}) gt 100000");

        Assert.DoesNotContain("Nowhere", within);
        Assert.DoesNotContain("Nowhere", beyond);
    }

    [Fact]
    public void GeoDistance_CombinedWithOtherFilters_AppliesBoth()
    {
        var names = SearchWithFilter($"geo.distance(Location, {Seattle}) le 50 and Population gt 200000");

        // Bellevue is close enough but too small.
        Assert.Equal(["Seattle", "Tacoma"], names);
    }

    [Fact]
    public void GeoDistance_WithEqualsOperator_Throws()
    {
        // Exact equality against a computed distance is not meaningful, and Azure rejects it.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseFilter($"geo.distance(Location, {Seattle}) eq 10"));

        Assert.Contains("lt, le, gt, or ge", ex.Message);
    }

    [Fact]
    public void GeoDistance_WithoutComparison_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseFilter($"geo.distance(Location, {Seattle})"));

        Assert.Contains("kilometers", ex.Message);
    }

    [Fact]
    public void GeoDistance_OnNonGeographyField_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ParseFilter($"geo.distance(Population, {Seattle}) le 10"));

        Assert.Contains("Edm.GeographyPoint", ex.Message);
    }

    // ===== geo.intersects in $filter =====

    [Fact]
    public void GeoIntersects_ReturnsPointsInsidePolygon()
    {
        // A counterclockwise box around the Seattle/Bellevue area, excluding Tacoma.
        var names = SearchWithFilter(
            "geo.intersects(Location, geography'POLYGON((-122.5 47.5, -122.1 47.5, -122.1 47.7, -122.5 47.7, -122.5 47.5))')");

        Assert.Equal(["Bellevue", "Seattle"], names);
    }

    [Fact]
    public void GeoIntersects_PointOutsidePolygon_IsExcluded()
    {
        // A box around Portland only.
        var names = SearchWithFilter(
            "geo.intersects(Location, geography'POLYGON((-122.8 45.4, -122.5 45.4, -122.5 45.6, -122.8 45.6, -122.8 45.4))')");

        Assert.Equal(["Portland"], names);
    }

    [Fact]
    public void GeoIntersects_NullLocation_DoesNotMatch()
    {
        // A polygon covering effectively the whole globe still must not match a null point.
        var names = SearchWithFilter(
            "geo.intersects(Location, geography'POLYGON((-180 -90, 180 -90, 180 90, -180 90, -180 -90))')");

        Assert.DoesNotContain("Nowhere", names);
    }

    [Fact]
    public void GeoIntersects_ClockwiseRing_StillMatches()
    {
        // Azure documents counterclockwise winding, but the point-in-polygon test is
        // orientation-independent, so a clockwise ring is accepted rather than rejected.
        var names = SearchWithFilter(
            "geo.intersects(Location, geography'POLYGON((-122.5 47.5, -122.5 47.7, -122.1 47.7, -122.1 47.5, -122.5 47.5))')");

        Assert.Equal(["Bellevue", "Seattle"], names);
    }

    [Fact]
    public void GeoIntersects_UnclosedPolygon_Throws()
    {
        // The OData parser rejects an unclosed ring while reading the literal, so the error
        // surfaces before GeoSupport.ValidateRing ever sees it.
        var ex = Assert.Throws<ODataException>(() => ParseFilter(
            "geo.intersects(Location, geography'POLYGON((-122.5 47.5, -122.1 47.5, -122.1 47.7, -122.5 47.7))')"));

        Assert.Contains("last point must be equal to the first point", ex.Message);
    }

    [Fact]
    public void GeoIntersects_OnNonGeographyField_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ParseFilter(
            "geo.intersects(Population, geography'POLYGON((-122.5 47.5, -122.1 47.5, -122.1 47.7, -122.5 47.7, -122.5 47.5))')"));

        Assert.Contains("Edm.GeographyPoint", ex.Message);
    }

    // ===== Field creation and round-tripping =====

    [Fact]
    public void CreateFields_ParsesGeoJsonPoint()
    {
        var field = new SearchField { Name = "Location", Type = "Edm.GeographyPoint" };
        var value = JsonNode.Parse("""{"type":"Point","coordinates":[-122.3321,47.6062]}""")!;

        var fields = field.CreateFields(value).ToList();

        var lat = fields.Single(i => i.Name == GeoSupport.GetLatFieldName("Location"));
        var lon = fields.Single(i => i.Name == GeoSupport.GetLonFieldName("Location"));

        Assert.Equal(47.6062, lat.GetDoubleValue()!.Value, 6);
        Assert.Equal(-122.3321, lon.GetDoubleValue()!.Value, 6);
    }

    [Fact]
    public void CreateFields_WithWrongGeoJsonType_Throws()
    {
        var field = new SearchField { Name = "Location", Type = "Edm.GeographyPoint" };
        var value = JsonNode.Parse("""{"type":"LineString","coordinates":[[0,0],[1,1]]}""")!;

        var ex = Assert.Throws<InvalidOperationException>(() => field.CreateFields(value).ToList());

        Assert.Contains("expected 'Point'", ex.Message);
    }

    [Fact]
    public void CreateFields_WithOutOfRangeLatitude_Throws()
    {
        var field = new SearchField { Name = "Location", Type = "Edm.GeographyPoint" };
        var value = JsonNode.Parse("""{"type":"Point","coordinates":[0,91]}""")!;

        var ex = Assert.Throws<InvalidOperationException>(() => field.CreateFields(value).ToList());

        Assert.Contains("latitude", ex.Message);
    }

    [Fact]
    public void CreateGeoJsonPoint_UsesLongitudeLatitudeOrder()
    {
        var json = GeoSupport.CreateGeoJsonPoint(-122.3321, 47.6062);

        Assert.Equal("Point", json["type"]!.GetValue<string>());
        Assert.Equal(-122.3321, json["coordinates"]![0]!.GetValue<double>(), 6);
        Assert.Equal(47.6062, json["coordinates"]![1]!.GetValue<double>(), 6);
    }

    // ===== $orderby and result round-tripping =====

    /// <summary>
    /// Indexes documents through the real CreateFields path and searches them through the
    /// real searcher, so $orderby and result conversion are exercised end to end.
    /// </summary>
    private static (LuceneTestHelper Helper, LuceneNetIndexSearcher Searcher) BuildSearcher(
        SearchIndex index,
        List<JsonObject> docs)
    {
        var luceneDocs = docs.Select(d =>
        {
            var doc = new Lucene.Net.Documents.Document();

            foreach (var field in index.Fields)
            {
                if (d[field.Name] is { } value)
                {
                    foreach (var f in field.CreateFields(value))
                    {
                        doc.Add(f);
                    }
                }
            }

            return doc;
        }).ToList();

        var helper = new LuceneTestHelper(index, luceneDocs);

        return (helper, new LuceneNetIndexSearcher(new StubReaderFactory(helper.Directory)));
    }

    private static SearchIndex CityIndex => LuceneTestHelper.CreateCityIndex();

    private static List<JsonObject> CityDocs =>
    [
        new() { ["Id"] = "1", ["Name"] = "Seattle", ["Location"] = Point(-122.3321, 47.6062), ["Population"] = 737015 },
        new() { ["Id"] = "2", ["Name"] = "Bellevue", ["Location"] = Point(-122.2015, 47.6101), ["Population"] = 148164 },
        new() { ["Id"] = "3", ["Name"] = "Portland", ["Location"] = Point(-122.6784, 45.5152), ["Population"] = 652503 },
        // No Location, to cover how null points sort.
        new() { ["Id"] = "4", ["Name"] = "Nowhere", ["Population"] = 1000 },
    ];

    private static JsonObject Point(double lon, double lat) =>
        new() { ["type"] = "Point", ["coordinates"] = new JsonArray(lon, lat) };

    [Fact]
    public async Task OrderBy_GeoDistanceAscending_SortsNearestFirst()
    {
        var (helper, searcher) = BuildSearcher(CityIndex, CityDocs);
        using var _ = helper;

        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = $"geo.distance(Location, {Seattle}) asc",
            Top = 50
        });

        var names = response.Results.Select(r => r["Name"]?.GetValue<string>()).ToList();

        // Nowhere has no location, so it sorts last under asc.
        Assert.Equal(["Seattle", "Bellevue", "Portland", "Nowhere"], names);
    }

    [Fact]
    public async Task OrderBy_GeoDistanceDescending_SortsFarthestFirst()
    {
        var (helper, searcher) = BuildSearcher(CityIndex, CityDocs);
        using var _ = helper;

        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = $"geo.distance(Location, {Seattle}) desc",
            Top = 50
        });

        var names = response.Results.Select(r => r["Name"]?.GetValue<string>()).ToList();

        // A null location sorts as the maximum distance, so it leads under desc.
        Assert.Equal(["Nowhere", "Portland", "Bellevue", "Seattle"], names);
    }

    [Fact]
    public async Task OrderBy_GeoDistanceAfterAnotherField_ParsesBothClauses()
    {
        var (helper, searcher) = BuildSearcher(CityIndex, CityDocs);
        using var _ = helper;

        // The comma inside geo.distance's arguments must not be mistaken for a clause
        // separator when the expression is split.
        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = $"Population desc,geo.distance(Location, {Seattle}) asc",
            Top = 50
        });

        var names = response.Results.Select(r => r["Name"]?.GetValue<string>()).ToList();

        Assert.Equal(["Seattle", "Portland", "Bellevue", "Nowhere"], names);
    }

    [Theory]
    [InlineData("asc", new[] { "00", "01", "02", "03", "04" })]
    [InlineData("desc", new[] { "19", "18", "17", "16", "15" })]
    public async Task OrderBy_GeoDistanceWithSmallTop_KeepsTheCorrectHits(string direction, string[] expected)
    {
        // Requesting fewer hits than there are matches makes Lucene evict from its priority
        // queue as it goes, which is the only path that exercises the comparer's bottom
        // comparison. An inverted sign here would silently keep the wrong end of the range.
        var index = new SearchIndex
        {
            Name = "points",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true, Sortable = true },
            ]
        };

        // Twenty points marching east, so distance from the origin increases with the id.
        var docs = Enumerable.Range(0, 20)
            .Select(i => new JsonObject
            {
                ["Id"] = i.ToString("00"),
                ["Location"] = Point(i * 0.5, 0)
            })
            .ToList();

        var (helper, searcher) = BuildSearcher(index, docs);
        using var _ = helper;

        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = $"geo.distance(Location, geography'POINT(0 0)') {direction}",
            Top = 5
        });

        Assert.Equal(expected, response.Results.Select(r => r["Id"]?.GetValue<string>()));
    }

    [Fact]
    public async Task OrderBy_GeoDistanceOnNonSortableField_Throws()
    {
        var index = LuceneTestHelper.CreateCityIndex();
        index.Fields.Single(i => i.Name == "Location").Sortable = false;

        var (helper, searcher) = BuildSearcher(index, CityDocs);
        using var _ = helper;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = $"geo.distance(Location, {Seattle}) asc",
            Top = 50
        }));

        Assert.Contains("not sortable", ex.Message);
    }

    [Fact]
    public async Task Search_ReturnsGeographyPointAsGeoJson()
    {
        var (helper, searcher) = BuildSearcher(CityIndex, CityDocs);
        using var _ = helper;

        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = "Name eq 'Seattle'",
            Top = 50
        });

        var location = Assert.Single(response.Results)["Location"];

        Assert.NotNull(location);
        Assert.Equal("Point", location!["type"]?.GetValue<string>());
        Assert.Equal(-122.3321, location["coordinates"]![0]!.GetValue<double>(), 6);
        Assert.Equal(47.6062, location["coordinates"]![1]!.GetValue<double>(), 6);
    }

    [Fact]
    public async Task Search_OmitsGeographyPointWhenNotSet()
    {
        var (helper, searcher) = BuildSearcher(CityIndex, CityDocs);
        using var _ = helper;

        var response = await searcher.Search(helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = "Name eq 'Nowhere'",
            Top = 50
        });

        Assert.Null(Assert.Single(response.Results)["Location"]);
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }

    // ===== Edge cases in the bounding-box prefilter =====

    /// <summary>
    /// Builds a throwaway index holding just the given points, for the geometry edge cases.
    /// </summary>
    private static List<string> SearchPoints(string filter, params (string Id, double Lon, double Lat)[] points)
    {
        var index = new SearchIndex
        {
            Name = "points",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true, Sortable = true },
            ]
        };

        var docs = points.Select(p =>
        {
            var doc = new Lucene.Net.Documents.Document
            {
                new Lucene.Net.Documents.StringField("Id", p.Id, Lucene.Net.Documents.Field.Store.YES)
            };

            foreach (var f in index.Fields[1].CreateFields(Point(p.Lon, p.Lat)))
            {
                doc.Add(f);
            }

            return doc;
        }).ToList();

        using var helper = new LuceneTestHelper(index, docs);
        var searcher = helper.CreateSearcher();

        var query = new UriQueryExpressionParser(100).ParseFilter(filter).Accept(new ODataQueryVisitor(index));

        return searcher.Search(query, 100).ScoreDocs
            .Select(sd => searcher.Doc(sd.Doc).Get("Id"))
            .OrderBy(id => id)
            .ToList();
    }

    [Fact]
    public void GeoDistance_AcrossTheAntimeridian_MatchesBothSides()
    {
        // The candidate bounding box wraps past 180 degrees, so it has to be searched as
        // two longitude spans rather than one.
        var ids = SearchPoints(
            "geo.distance(Location, geography'POINT(180 0)') le 100",
            ("east", 179.7, 0.0),
            ("west", -179.7, 0.0));

        Assert.Equal(["east", "west"], ids);
    }

    [Fact]
    public void GeoDistance_NearThePole_MatchesRegardlessOfLongitude()
    {
        // Near the poles, points at opposite longitudes are physically close, so the
        // bounding box has to widen to the full longitude range.
        var ids = SearchPoints(
            "geo.distance(Location, geography'POINT(0 90)') le 50",
            ("a", 0, 89.9),
            ("b", 180, 89.9));

        Assert.Equal(["a", "b"], ids);
    }

    [Fact]
    public void GeoIntersects_PolygonAcrossTheAntimeridian_MatchesTheNarrowBand()
    {
        // The rectangle Microsoft documents as the supported dateline case. Read naively,
        // its longitudes run from -179 to 179 and would select everything except the band
        // it is meant to cover, so the ring has to be unrolled before it is used.
        var ids = SearchPoints(
            "geo.intersects(Location, geography'POLYGON((179 65, 179 66, -179 66, -179 65, 179 65))')",
            ("east", 179.5, 65.5),
            ("west", -179.5, 65.5),
            ("far", 0.0, 65.5));

        Assert.Equal(["east", "west"], ids);
    }

    [Fact]
    public void GeoDistance_LargerThanHalfTheGlobe_MatchesEverything()
    {
        var ids = SearchPoints(
            "geo.distance(Location, geography'POINT(0 0)') le 25000",
            ("a", 0, 0),
            ("b", 179, 0));

        Assert.Equal(["a", "b"], ids);
    }

    // ===== Iterator contract =====

    [Fact]
    public void GeoDocIdSet_StaysExhausted()
    {
        // NextDoc() past the end computes Advance(NO_MORE_DOCS + 1), which overflows to
        // int.MinValue. Without a guard the scan restarts from the bottom and re-emits every
        // match, so exhaustion has to be sticky.
        var set = new GeoDocIdSet(3, acceptDocs: null, match: _ => true);
        var iterator = set.GetIterator();

        Assert.Equal(-1, iterator.DocID);
        Assert.Equal(0, iterator.NextDoc());
        Assert.Equal(1, iterator.NextDoc());
        Assert.Equal(2, iterator.NextDoc());
        Assert.Equal(DocIdSetIterator.NO_MORE_DOCS, iterator.NextDoc());

        // Any further call must keep reporting exhaustion.
        Assert.Equal(DocIdSetIterator.NO_MORE_DOCS, iterator.NextDoc());
        Assert.Equal(DocIdSetIterator.NO_MORE_DOCS, iterator.Advance(0));
    }

    [Fact]
    public void GeoDocIdSet_AdvancePastEnd_ReportsExhaustion()
    {
        var iterator = new GeoDocIdSet(3, acceptDocs: null, match: _ => true).GetIterator();

        Assert.Equal(DocIdSetIterator.NO_MORE_DOCS, iterator.Advance(3));
    }

    // ===== Collection(Edm.GeographyPoint) =====

    [Fact]
    public void CreateFields_ForCollectionOfPoints_ThrowsRatherThanIndexingSilently()
    {
        // Multi-valued points are not supported yet. Indexing them would appear to work but
        // filters would then match against only one of the points, so this fails loudly.
        var field = new SearchField { Name = "Locations", Type = "Collection(Edm.GeographyPoint)" };
        var value = JsonNode.Parse("""[{"type":"Point","coordinates":[0,0]},{"type":"Point","coordinates":[1,1]}]""")!;

        var ex = Assert.Throws<NotImplementedException>(() => field.CreateFields(value).ToList());

        Assert.Contains("Collection(Edm.GeographyPoint)", ex.Message);
    }

    [Fact]
    public void GeoDistance_OnCollectionField_Throws()
    {
        var index = LuceneTestHelper.CreateCityIndex();
        index.Fields.Add(new SearchField
        {
            Name = "Locations",
            Type = "Collection(Edm.GeographyPoint)",
            Filterable = true
        });

        var parser = new UriQueryExpressionParser(100);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            parser.ParseFilter($"geo.distance(Locations, {Seattle}) le 10").Accept(new ODataQueryVisitor(index)));

        Assert.Contains("Collection(Edm.GeographyPoint)", ex.Message);
    }

    // ===== Distance math =====

    [Fact]
    public void GetDistanceKm_MatchesKnownDistance()
    {
        // Seattle to Portland is about 234km.
        var distance = GeoSupport.GetDistanceKm(-122.3321, 47.6062, -122.6784, 45.5152);

        Assert.Equal(234, distance, 0);
    }

    [Fact]
    public void GetDistanceKm_IsZeroForSamePoint()
    {
        Assert.Equal(0, GeoSupport.GetDistanceKm(-122.3321, 47.6062, -122.3321, 47.6062), 6);
    }
}
