using System.Text.Json.Nodes;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Spatial4n.Context;
using Spatial4n.Distance;
using Spatial4n.Shapes;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Helpers for the <c>Edm.GeographyPoint</c> field type and the <c>geo.distance</c> /
/// <c>geo.intersects</c> OData functions.
/// </summary>
/// <remarks>
/// Azure Search uses two different representations for geography points, and this class is
/// the single place that bridges them:
/// <list type="bullet">
/// <item>Document bodies use GeoJSON, i.e. <c>{ "type": "Point", "coordinates": [lon, lat] }</c>.</item>
/// <item>Filter/order-by expressions use WKT literals, i.e. <c>geography'POINT(lon lat)'</c>.</item>
/// </list>
/// Both list longitude before latitude, which is the opposite of the conventional
/// "lat, lon" ordering, so the conversions here are deliberately explicit about it.
/// </remarks>
public static class GeoSupport
{
    public const string GeographyPointType = "Edm.GeographyPoint";

    /// <summary>
    /// Distances are reported in kilometers to match Azure Search. Note that this differs
    /// from most other OData services, which return meters.
    /// </summary>
    public const double EarthMeanRadiusKm = DistanceUtils.EarthMeanRadiusKilometers;

    private static readonly SpatialContext Context = SpatialContext.Geo;

    /// <summary>
    /// Latitude of a point, stored as a separate numeric field so it can be range-queried
    /// and read back without needing the spatial strategy.
    /// </summary>
    public static string GetLatFieldName(string fieldName) => "__azs_geo_lat__" + fieldName;

    /// <summary>
    /// Longitude counterpart to <see cref="GetLatFieldName"/>.
    /// </summary>
    public static string GetLonFieldName(string fieldName) => "__azs_geo_lon__" + fieldName;

    /// <summary>
    /// Parses a GeoJSON Point object from a document body.
    /// </summary>
    public static (double Lon, double Lat) ParseGeoJsonPoint(string fieldName, JsonNode value)
    {
        if (value is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' is type {GeographyPointType} but received a non-object JSON value.");
        }

        var type = obj["type"]?.GetValue<string>();

        if (!string.Equals(type, "Point", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' is type {GeographyPointType} but received GeoJSON type '{type}'; expected 'Point'.");
        }

        if (obj["coordinates"] is not JsonArray coordinates || coordinates.Count < 2)
        {
            throw new InvalidOperationException(
                $"Field '{fieldName}' is type {GeographyPointType} but its GeoJSON 'coordinates' must be an array of [longitude, latitude].");
        }

        // GeoJSON orders coordinates as [longitude, latitude].
        var lon = coordinates[0]!.GetValue<double>();
        var lat = coordinates[1]!.GetValue<double>();

        ValidateCoordinates(fieldName, lon, lat);

        return (lon, lat);
    }

    /// <summary>
    /// Builds the GeoJSON Point object returned in search results and document lookups.
    /// </summary>
    public static JsonNode CreateGeoJsonPoint(double lon, double lat) =>
        new JsonObject
        {
            ["type"] = "Point",
            ["coordinates"] = new JsonArray(lon, lat)
        };

    /// <summary>
    /// Creates the Lucene fields backing a single geography point.
    /// </summary>
    public static IEnumerable<IIndexableField> CreateFields(string fieldName, double lon, double lat, bool retrievable)
    {
        // Indexed as a doc-values-backed numeric pair: DoubleField supports the range
        // queries used to pre-filter candidates, and the stored copies let results be
        // reconstructed without a second lookup.
        var stored = retrievable ? Field.Store.YES : Field.Store.NO;

        yield return new DoubleField(GetLatFieldName(fieldName), lat, stored);
        yield return new DoubleField(GetLonFieldName(fieldName), lon, stored);
    }

    /// <summary>
    /// Validates the bounding ring of a polygon used with <c>geo.intersects</c>.
    /// </summary>
    /// <remarks>
    /// Azure Search requires the ring to be closed (first point repeated last) and to be
    /// wound counterclockwise. Closure is validated because an unclosed ring is almost
    /// always a caller bug, but winding order deliberately is not: the point-in-polygon
    /// test is orientation-independent, so rejecting clockwise rings would only fail
    /// queries that would otherwise return the expected results.
    /// </remarks>
    public static IReadOnlyList<(double Lon, double Lat)> ValidateRing(IReadOnlyList<(double Lon, double Lat)> ring)
    {
        if (ring.Count < 4)
        {
            throw new InvalidOperationException(
                "A polygon must have at least four points, where the first and last are the same.");
        }

        // Coordinates come straight from the literal without arithmetic applied, so the
        // first and last points of a closed ring are bit-identical rather than merely close.
        if (ring[0].Lon != ring[^1].Lon || ring[0].Lat != ring[^1].Lat)
        {
            throw new InvalidOperationException(
                "A polygon must be closed; its first and last points must be the same.");
        }

        foreach (var point in ring)
        {
            ValidateCoordinates("geography'POLYGON(...)'", point.Lon, point.Lat);
        }

        return ring;
    }

    /// <summary>
    /// Great-circle distance in kilometers between two points.
    /// </summary>
    public static double GetDistanceKm(double lon1, double lat1, double lon2, double lat2)
    {
        var degrees = DistanceUtils.DistHaversineRAD(
            DistanceUtils.ToRadians(lat1),
            DistanceUtils.ToRadians(lon1),
            DistanceUtils.ToRadians(lat2),
            DistanceUtils.ToRadians(lon2));

        return DistanceUtils.Radians2Dist(degrees, EarthMeanRadiusKm);
    }

    /// <summary>
    /// Converts a distance in kilometers to the latitude/longitude degree span it covers,
    /// used to build the bounding box that pre-filters candidate documents.
    /// </summary>
    public static IRectangle GetBoundingBox(double lon, double lat, double distanceKm)
    {
        var degrees = DistanceUtils.Dist2Degrees(distanceKm, EarthMeanRadiusKm);
        return Context.MakeCircle(lon, lat, degrees).BoundingBox;
    }

    /// <summary>
    /// Ray-casting point-in-polygon test over the ring's longitude/latitude plane.
    /// </summary>
    /// <remarks>
    /// This treats the ring edges as straight lines in lon/lat space rather than as great
    /// circles, which matches how Azure Search behaves for the viewport-sized polygons these
    /// filters are used for. Rings that cross the antimeridian are unrolled onto a
    /// continuous longitude axis first, so a rectangle like
    /// <c>POLYGON((179 65, 179 66, -179 66, -179 65, 179 65))</c> is treated as the narrow
    /// band around the dateline rather than as a band spanning the rest of the globe.
    /// </remarks>
    public static bool IsPointInPolygon(IReadOnlyList<(double Lon, double Lat)> ring, double lon, double lat)
    {
        var unrolled = Unroll(ring);

        // The ring may now extend past 180 degrees, so the test point has to be shifted onto
        // the same revolution before it is compared.
        var (minLon, maxLon, _, _) = GetPolygonBounds(unrolled);

        if (lon < minLon && lon + 360 <= maxLon)
        {
            lon += 360;
        }
        else if (lon > maxLon && lon - 360 >= minLon)
        {
            lon -= 360;
        }

        var inside = false;

        for (int i = 0, j = unrolled.Count - 1; i < unrolled.Count; j = i++)
        {
            var (xi, yi) = (unrolled[i].Lon, unrolled[i].Lat);
            var (xj, yj) = (unrolled[j].Lon, unrolled[j].Lat);

            // Does the edge straddle the test point's latitude, and if so, is the crossing
            // to the right of the point?
            if (yi > lat != yj > lat
                && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Rewrites a ring that crosses the antimeridian onto a continuous longitude axis, so
    /// that consecutive points never jump by more than 180 degrees. Longitudes may end up
    /// outside the normal -180..180 range as a result.
    /// </summary>
    /// <remarks>
    /// A ring that does not cross the dateline is returned unchanged.
    /// </remarks>
    public static IReadOnlyList<(double Lon, double Lat)> Unroll(IReadOnlyList<(double Lon, double Lat)> ring)
    {
        var unrolled = new List<(double Lon, double Lat)>(ring.Count) { ring[0] };
        var offset = 0d;

        for (var i = 1; i < ring.Count; i++)
        {
            var delta = ring[i].Lon - ring[i - 1].Lon;

            // A jump of more than half the globe between adjacent points is the dateline
            // being crossed, not a genuine traversal the long way around.
            if (delta > 180)
            {
                offset -= 360;
            }
            else if (delta < -180)
            {
                offset += 360;
            }

            unrolled.Add((ring[i].Lon + offset, ring[i].Lat));
        }

        return unrolled;
    }

    /// <summary>
    /// Axis-aligned bounds of a ring, used to pre-filter candidates before the exact test.
    /// </summary>
    /// <remarks>
    /// The bounds are taken from the unrolled ring, so for an antimeridian-spanning polygon
    /// the returned longitudes can fall outside -180..180. Callers are expected to split
    /// such a range into two spans either side of the dateline.
    /// </remarks>
    public static (double MinLon, double MaxLon, double MinLat, double MaxLat) GetPolygonBounds(
        IReadOnlyList<(double Lon, double Lat)> ring) =>
        (ring.Min(i => i.Lon), ring.Max(i => i.Lon), ring.Min(i => i.Lat), ring.Max(i => i.Lat));

    private static void ValidateCoordinates(string context, double lon, double lat)
    {
        if (double.IsNaN(lon) || double.IsNaN(lat) || double.IsInfinity(lon) || double.IsInfinity(lat))
        {
            throw new InvalidOperationException($"{context} has a non-finite coordinate.");
        }

        if (lat is < -90 or > 90)
        {
            throw new InvalidOperationException($"{context} has latitude {lat}, which is outside the range -90 to 90.");
        }

        if (lon is < -180 or > 180)
        {
            throw new InvalidOperationException($"{context} has longitude {lon}, which is outside the range -180 to 180.");
        }
    }
}
