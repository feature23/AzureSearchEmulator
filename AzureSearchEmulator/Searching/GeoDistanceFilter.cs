using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Applies the exact haversine distance test for <see cref="GeoDistanceQuery"/>, after a
/// bounding box has narrowed the candidate set.
/// </summary>
/// <remarks>
/// Multi-valued fields (<c>Collection(Edm.GeographyPoint)</c>) match if <em>any</em> of the
/// document's points satisfies the predicate, which mirrors how Azure Search evaluates a
/// collection under <c>geo.distance</c>.
/// </remarks>
public class GeoDistanceFilter(
    string fieldName,
    double originLon,
    double originLat,
    double distanceKm,
    bool withinDistance,
    bool inclusive) : Filter
{
    public override DocIdSet GetDocIdSet(AtomicReaderContext context, IBits acceptDocs)
    {
        var reader = context.AtomicReader;

        var getPoints = GeoSupport.GetPointReader(reader, fieldName);

        return new PredicateDocIdSet(reader.MaxDoc, acceptDocs, doc =>
        {
            var points = getPoints(doc);

            // A null point never matches, in either direction. Azure Search treats
            // geo.distance over a null field as null, which fails every comparison. An empty
            // collection behaves the same way, since there is no element to satisfy the
            // predicate.
            if (points.Count == 0)
            {
                return false;
            }

            // A match on any single point is enough: Azure only allows this filter inside an
            // any() lambda for collections, whose semantics are exactly "some element
            // satisfies the predicate". The all() case is compiled as ¬any(¬P) higher up, so
            // it also arrives here as an existential test.
            foreach (var (lon, lat) in points)
            {
                var distance = GeoSupport.GetDistanceKm(lon, lat, originLon, originLat);

                // Each operator is tested directly rather than by negating its opposite: the
                // inverse of "< d" is ">= d", so flipping the result of an exclusive test would
                // silently make the boundary inclusive.
                var matches = (withinDistance, inclusive) switch
                {
                    (true, true) => distance <= distanceKm,   // le
                    (true, false) => distance < distanceKm,   // lt
                    (false, true) => distance >= distanceKm,  // ge
                    (false, false) => distance > distanceKm   // gt
                };

                if (matches)
                {
                    return true;
                }
            }

            return false;
        });
    }

    public override string ToString() =>
        $"GeoDistanceFilter({fieldName}, {originLon} {originLat}, {distanceKm}km)";
}
