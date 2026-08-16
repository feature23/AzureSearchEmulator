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

        // SortedNumericDocValues would be the natural fit for multi-valued points, but the
        // documents are indexed with DoubleField, so the values are read back from the
        // per-segment numeric doc values that Lucene builds for them.
        var lats = FieldCache.DEFAULT.GetDoubles(reader, GeoSupport.GetLatFieldName(fieldName), true);
        var lons = FieldCache.DEFAULT.GetDoubles(reader, GeoSupport.GetLonFieldName(fieldName), true);
        var hasValue = FieldCache.DEFAULT.GetDocsWithField(reader, GeoSupport.GetLatFieldName(fieldName));

        return new GeoDocIdSet(reader.MaxDoc, acceptDocs, doc =>
        {
            // A null point never matches, in either direction. Azure Search treats
            // geo.distance over a null field as null, which fails every comparison.
            if (!hasValue.Get(doc))
            {
                return false;
            }

            var distance = GeoSupport.GetDistanceKm(lons.Get(doc), lats.Get(doc), originLon, originLat);

            // Each operator is tested directly rather than by negating its opposite: the
            // inverse of "< d" is ">= d", so flipping the result of an exclusive test would
            // silently make the boundary inclusive.
            return (withinDistance, inclusive) switch
            {
                (true, true) => distance <= distanceKm,   // le
                (true, false) => distance < distanceKm,   // lt
                (false, true) => distance >= distanceKm,  // ge
                (false, false) => distance > distanceKm   // gt
            };
        });
    }

    public override string ToString() =>
        $"GeoDistanceFilter({fieldName}, {originLon} {originLat}, {distanceKm}km)";
}
