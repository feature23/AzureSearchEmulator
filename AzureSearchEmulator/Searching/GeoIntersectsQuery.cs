using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Matches documents whose geography point falls inside a polygon, implementing
/// <c>geo.intersects(field, geography'POLYGON((...))')</c>.
/// </summary>
/// <remarks>
/// Like <see cref="GeoDistanceQuery"/>, this narrows candidates with a numeric range query
/// over the polygon's bounding box before running the exact point-in-polygon test.
/// </remarks>
public class GeoIntersectsQuery(string fieldName, IReadOnlyList<(double Lon, double Lat)> ring) : Query
{
    public override Query Rewrite(IndexReader reader)
    {
        var (minLon, maxLon, minLat, maxLat) = GeoSupport.GetPolygonBounds(ring);

        var candidates = new BooleanQuery
        {
            {
                NumericRangeQuery.NewDoubleRange(
                    GeoSupport.GetLatFieldName(fieldName), minLat, maxLat, true, true),
                Occur.MUST
            },
            {
                NumericRangeQuery.NewDoubleRange(
                    GeoSupport.GetLonFieldName(fieldName), minLon, maxLon, true, true),
                Occur.MUST
            }
        };

        var exact = new FilteredQuery(
            new ConstantScoreQuery(candidates),
            new GeoIntersectsFilter(fieldName, ring));

        exact.Boost = Boost;

        return exact;
    }

    public override string ToString(string field) => $"geo.intersects({fieldName}, POLYGON with {ring.Count} points)";

    private string FieldName => fieldName;

    private IReadOnlyList<(double Lon, double Lat)> Ring => ring;

    public override bool Equals(object? obj) =>
        obj is GeoIntersectsQuery other && fieldName == other.FieldName && ring.SequenceEqual(other.Ring);

    public override int GetHashCode() => HashCode.Combine(fieldName, ring.Count);
}

/// <summary>
/// Applies the exact point-in-polygon test for <see cref="GeoIntersectsQuery"/>.
/// </summary>
public class GeoIntersectsFilter(string fieldName, IReadOnlyList<(double Lon, double Lat)> ring) : Filter
{
    public override DocIdSet GetDocIdSet(AtomicReaderContext context, IBits acceptDocs)
    {
        var reader = context.AtomicReader;

        var lats = FieldCache.DEFAULT.GetDoubles(reader, GeoSupport.GetLatFieldName(fieldName), true);
        var lons = FieldCache.DEFAULT.GetDoubles(reader, GeoSupport.GetLonFieldName(fieldName), true);
        var hasValue = FieldCache.DEFAULT.GetDocsWithField(reader, GeoSupport.GetLatFieldName(fieldName));

        return new GeoDocIdSet(reader.MaxDoc, acceptDocs, doc =>
            // Azure Search evaluates geo.intersects over a null field as false.
            hasValue.Get(doc) && GeoSupport.IsPointInPolygon(ring, lons.Get(doc), lats.Get(doc)));
    }

    public override string ToString() => $"GeoIntersectsFilter({fieldName}, {ring.Count} points)";
}
