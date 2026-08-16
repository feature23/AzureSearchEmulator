using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Matches documents whose geography point lies within (or beyond) a given distance of an
/// origin, implementing <c>geo.distance(field, geography'POINT(lon lat)') le 10</c>.
/// </summary>
/// <remarks>
/// Evaluated in two stages. A numeric range query on the stored latitude/longitude narrows
/// candidates to a bounding box, which Lucene can serve from its numeric index; the exact
/// haversine distance is then applied per candidate. The bounding box is a superset of the
/// true circle, so the second stage only ever removes matches, never adds them.
/// </remarks>
public class GeoDistanceQuery : Query
{
    private readonly string _fieldName;
    private readonly double _originLon;
    private readonly double _originLat;
    private readonly double _distanceKm;
    private readonly bool _inclusive;
    private readonly bool _withinDistance;

    /// <param name="withinDistance">
    /// <c>true</c> for <c>lt</c>/<c>le</c> (inside the circle), <c>false</c> for
    /// <c>gt</c>/<c>ge</c> (outside it).
    /// </param>
    /// <param name="inclusive"><c>true</c> for the "or equal" variants.</param>
    public GeoDistanceQuery(
        string fieldName,
        double originLon,
        double originLat,
        double distanceKm,
        bool withinDistance,
        bool inclusive)
    {
        _fieldName = fieldName;
        _originLon = originLon;
        _originLat = originLat;
        _distanceKm = distanceKm;
        _withinDistance = withinDistance;
        _inclusive = inclusive;
    }

    public override Query Rewrite(IndexReader reader)
    {
        var latField = GeoSupport.GetLatFieldName(_fieldName);
        var lonField = GeoSupport.GetLonFieldName(_fieldName);

        Query candidates;

        if (_withinDistance)
        {
            var box = GeoSupport.GetBoundingBox(_originLon, _originLat, _distanceKm);

            var latRange = NumericRangeQuery.NewDoubleRange(latField, box.MinY, box.MaxY, true, true);

            // A bounding box that wraps the antimeridian has MinX > MaxX, so it has to be
            // matched as two spans either side of the dateline.
            Query lonRange = box.MinX <= box.MaxX
                ? NumericRangeQuery.NewDoubleRange(lonField, box.MinX, box.MaxX, true, true)
                : new BooleanQuery
                {
                    { NumericRangeQuery.NewDoubleRange(lonField, box.MinX, 180d, true, true), Occur.SHOULD },
                    { NumericRangeQuery.NewDoubleRange(lonField, -180d, box.MaxX, true, true), Occur.SHOULD }
                };

            candidates = new BooleanQuery
            {
                { latRange, Occur.MUST },
                { lonRange, Occur.MUST }
            };
        }
        else
        {
            // "Farther than" can't be narrowed by a box, so every document that has the
            // field is a candidate and the distance check does the filtering.
            candidates = NumericRangeQuery.NewDoubleRange(
                latField, double.NegativeInfinity, double.PositiveInfinity, true, true);
        }

        var exact = new FilteredQuery(
            new ConstantScoreQuery(candidates),
            new GeoDistanceFilter(_fieldName, _originLon, _originLat, _distanceKm, _withinDistance, _inclusive));

        exact.Boost = Boost;

        return exact;
    }

    public override string ToString(string field) =>
        $"geo.distance({_fieldName}, POINT({_originLon} {_originLat})) {(_withinDistance ? "<" : ">")}{(_inclusive ? "=" : "")} {_distanceKm}";

    public override bool Equals(object? obj) =>
        obj is GeoDistanceQuery other
        && _fieldName == other._fieldName
        && _originLon.Equals(other._originLon)
        && _originLat.Equals(other._originLat)
        && _distanceKm.Equals(other._distanceKm)
        && _withinDistance == other._withinDistance
        && _inclusive == other._inclusive;

    public override int GetHashCode() =>
        HashCode.Combine(_fieldName, _originLon, _originLat, _distanceKm, _withinDistance, _inclusive);
}
