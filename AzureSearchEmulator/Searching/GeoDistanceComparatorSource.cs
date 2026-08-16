using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Sorts documents by their distance from an origin, backing
/// <c>$orderby=geo.distance(field, geography'POINT(lon lat)') asc</c>.
/// </summary>
/// <remarks>
/// Documents whose point is null sort as the maximum possible distance, which places them
/// after all other documents under <c>asc</c> and before them under <c>desc</c>. This
/// matches how Azure Search orders missing geography values.
/// </remarks>
public class GeoDistanceComparatorSource(string fieldName, double originLon, double originLat)
    : FieldComparerSource
{
    public override FieldComparer NewComparer(string fieldName1, int numHits, int sortPos, bool reversed) =>
        new Comparer(fieldName, originLon, originLat, numHits);

    // FieldComparer<T> requires a reference type, so the distances are boxed through J2N's
    // Double the same way Lucene's own DoubleComparer does.
    private sealed class Comparer : FieldComparer<J2N.Numerics.Double>
    {
        private readonly string _fieldName;
        private readonly double _originLon;
        private readonly double _originLat;
        private readonly double[] _values;
        private double _bottom;
        private double _topValue;

        private FieldCache.Doubles? _lats;
        private FieldCache.Doubles? _lons;
        private IBits? _hasValue;

        public Comparer(string fieldName, double originLon, double originLat, int numHits)
        {
            _fieldName = fieldName;
            _originLon = originLon;
            _originLat = originLat;
            _values = new double[numHits];
        }

        public override int Compare(int slot1, int slot2) => _values[slot1].CompareTo(_values[slot2]);

        public override int CompareBottom(int doc) => _bottom.CompareTo(GetDistance(doc));

        public override void Copy(int slot, int doc) => _values[slot] = GetDistance(doc);

        public override void SetBottom(int slot) => _bottom = _values[slot];

        public override void SetTopValue(J2N.Numerics.Double value) => _topValue = value.ToDouble();

        public override int CompareTop(int doc) => _topValue.CompareTo(GetDistance(doc));

        public override FieldComparer SetNextReader(AtomicReaderContext context)
        {
            var reader = context.AtomicReader;
            var latField = GeoSupport.GetLatFieldName(_fieldName);

            _lats = FieldCache.DEFAULT.GetDoubles(reader, latField, true);
            _lons = FieldCache.DEFAULT.GetDoubles(reader, GeoSupport.GetLonFieldName(_fieldName), true);
            _hasValue = FieldCache.DEFAULT.GetDocsWithField(reader, latField);

            return this;
        }

        public override J2N.Numerics.Double this[int slot] => J2N.Numerics.Double.GetInstance(_values[slot]);

        private double GetDistance(int doc)
        {
            if (_hasValue?.Get(doc) != true)
            {
                return double.MaxValue;
            }

            return GeoSupport.GetDistanceKm(_lons!.Get(doc), _lats!.Get(doc), _originLon, _originLat);
        }
    }
}
