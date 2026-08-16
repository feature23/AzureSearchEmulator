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
        // Bounds are taken from the unrolled ring so that a polygon crossing the
        // antimeridian yields the narrow band around the dateline rather than its complement.
        var (minLon, maxLon, minLat, maxLat) = GeoSupport.GetPolygonBounds(GeoSupport.Unroll(ring));

        var lonField = GeoSupport.GetLonFieldName(fieldName);

        // Unrolling can push the bounds past 180 degrees, which no indexed longitude will
        // ever match directly, so the range is wrapped back and searched as two spans.
        Query lonRange;

        if (maxLon > 180)
        {
            lonRange = new BooleanQuery
            {
                { NumericRangeQuery.NewDoubleRange(lonField, minLon, 180d, true, true), Occur.SHOULD },
                { NumericRangeQuery.NewDoubleRange(lonField, -180d, maxLon - 360, true, true), Occur.SHOULD }
            };
        }
        else if (minLon < -180)
        {
            lonRange = new BooleanQuery
            {
                { NumericRangeQuery.NewDoubleRange(lonField, minLon + 360, 180d, true, true), Occur.SHOULD },
                { NumericRangeQuery.NewDoubleRange(lonField, -180d, maxLon, true, true), Occur.SHOULD }
            };
        }
        else
        {
            lonRange = NumericRangeQuery.NewDoubleRange(lonField, minLon, maxLon, true, true);
        }

        var candidates = new BooleanQuery
        {
            {
                NumericRangeQuery.NewDoubleRange(
                    GeoSupport.GetLatFieldName(fieldName), minLat, maxLat, true, true),
                Occur.MUST
            },
            { lonRange, Occur.MUST }
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
