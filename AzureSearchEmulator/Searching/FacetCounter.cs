using System.Globalization;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// One bucket of a facet: a value (or a range) and the number of matching documents in it.
/// </summary>
public sealed class FacetBucket
{
    /// <summary>
    /// The bucket's value, for a value facet. Null for a range bucket, which is identified by
    /// its bounds instead.
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// The inclusive lower bound of a range bucket, or null for the first bucket, which is
    /// open below.
    /// </summary>
    public object? From { get; init; }

    /// <summary>
    /// The exclusive upper bound of a range bucket, or null for the last bucket, which is
    /// open above.
    /// </summary>
    public object? To { get; init; }

    public required int Count { get; init; }
}

/// <summary>
/// Counts facet buckets over the documents a query matches.
/// </summary>
/// <remarks>
/// Facets are computed over the <em>whole</em> match set rather than the page of results being
/// returned, which is what makes them useful for navigation: <c>$top</c> and <c>$skip</c>
/// change which documents come back, never the counts. A <c>$filter</c> does narrow them,
/// since it narrows the match set itself.
///
/// Counting reads the doc values written by <see cref="FacetSupport"/>, so it sees a field's
/// exact values regardless of whether the field is analyzed, retrievable, or a collection. A
/// document is counted once per <em>distinct</em> value it holds for the field: a hotel whose
/// rooms are all deluxe counts once toward <c>Deluxe</c>, matching Azure Search, where facet
/// counts are of parent documents rather than of the sub-documents inside them.
/// </remarks>
public static class FacetCounter
{
    /// <summary>
    /// Computes every requested facet against the documents matching
    /// <paramref name="query"/> and <paramref name="filter"/>.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<FacetBucket>> Count(
        IndexSearcher searcher,
        IReadOnlyList<FacetRequest> facets,
        Query query,
        Filter? filter)
    {
        var collectors = facets.Select(f => new FacetValueCollector(f)).ToList();

        searcher.Search(query, filter, new MultiFacetCollector(collectors));

        var results = new Dictionary<string, IReadOnlyList<FacetBucket>>(StringComparer.Ordinal);

        foreach (var collector in collectors)
        {
            results[collector.Facet.Name] = collector.BuildBuckets();
        }

        return results;
    }

    /// <summary>
    /// The facet structure for a query that cannot match any document.
    /// </summary>
    /// <remarks>
    /// This is the same structure the counting pass would produce over an empty match set, and
    /// is built through the same collectors so that it cannot drift from it: a value facet
    /// ends up with no buckets, while a <c>values</c> facet keeps the full bucket scale its
    /// caller specified, every count at zero.
    /// </remarks>
    public static Dictionary<string, IReadOnlyList<FacetBucket>> Empty(
        IReadOnlyList<FacetRequest> facets)
    {
        var results = new Dictionary<string, IReadOnlyList<FacetBucket>>(StringComparer.Ordinal);

        foreach (var facet in facets)
        {
            results[facet.Name] = new FacetValueCollector(facet).BuildBuckets();
        }

        return results;
    }

    /// <summary>
    /// Fans one pass over the matching documents out to every facet's collector, so that N
    /// facets cost one walk of the match set rather than N.
    /// </summary>
    private sealed class MultiFacetCollector(IReadOnlyList<FacetValueCollector> collectors) : ICollector
    {
        public void SetScorer(Scorer scorer)
        {
        }

        public void Collect(int doc)
        {
            foreach (var collector in collectors)
            {
                collector.Collect(doc);
            }
        }

        public void SetNextReader(AtomicReaderContext context)
        {
            foreach (var collector in collectors)
            {
                collector.SetNextReader(context);
            }
        }

        // Facet counting never looks at the score, so out-of-order collection is safe and
        // lets Lucene use its faster scorers.
        public bool AcceptsDocsOutOfOrder => true;
    }

    /// <summary>
    /// Accumulates the raw per-value document counts for a single facet.
    /// </summary>
    private sealed class FacetValueCollector(FacetRequest facet)
    {
        /// <summary>
        /// Counts keyed by the encoded value, which keeps distinct values distinct without
        /// having to decode every value of every document. Used by value facets.
        /// </summary>
        private readonly Dictionary<BytesRef, int> _counts = new();

        /// <summary>
        /// The numbers each matching document holds, kept only for a range facet.
        /// </summary>
        /// <remarks>
        /// A range facet cannot accumulate into <see cref="_counts"/> the way a value facet
        /// does, because two <em>different</em> values of one document can land in the same
        /// bucket — a hotel with a 60 and a 65 room, against a bucket covering everything
        /// below 80. Azure Search counts the parent document once there, so the values have
        /// to stay grouped by document until the bounds are known and each document can be
        /// folded into the distinct buckets it touches.
        /// </remarks>
        private readonly List<double[]> _rangeDocuments = [];

        private Func<int, IReadOnlyList<BytesRef>> _reader = _ => [];

        public FacetRequest Facet => facet;

        public void SetNextReader(AtomicReaderContext context)
        {
            _reader = FacetSupport.GetValueReader(context.AtomicReader, facet.Path);
        }

        public void Collect(int doc)
        {
            var values = _reader(doc);

            if (values.Count == 0)
            {
                return;
            }

            if (facet.IsRange)
            {
                _rangeDocuments.Add(values.Select(FacetSupport.DecodeNumber).ToArray());
                return;
            }

            // Doc values are already deduplicated per document by the sorted set, so a
            // document that repeats a value — a hotel with two deluxe rooms — is counted once.
            foreach (var value in values)
            {
                _counts[value] = _counts.GetValueOrDefault(value) + 1;
            }
        }

        public IReadOnlyList<FacetBucket> BuildBuckets() =>
            facet.IsRange ? BuildRangeBuckets() : BuildValueBuckets();

        /// <summary>
        /// Builds one bucket per distinct value, ordered and capped as the facet asked.
        /// </summary>
        private IReadOnlyList<FacetBucket> BuildValueBuckets()
        {
            var elementType = GetElementType(facet.Field);

            var buckets = _counts
                .Select(kvp => (
                    Value: FacetSupport.ToBucketValue(elementType, kvp.Key),
                    Encoded: kvp.Key,
                    Count: kvp.Value))
                .Where(b => b.Value != null);

            // Value ordering uses the encoded bytes rather than the decoded value: they were
            // written so that their byte order is the value's own order, which keeps numbers
            // ordered numerically and strings ordinally without a per-type comparer here.
            var ordered = facet.Sort switch
            {
                FacetSort.CountAscending => buckets
                    .OrderBy(b => b.Count)
                    .ThenBy(b => b.Encoded, BytesRefComparer.Instance),
                FacetSort.ValueAscending => buckets
                    .OrderBy(b => b.Encoded, BytesRefComparer.Instance),
                FacetSort.ValueDescending => buckets
                    .OrderByDescending(b => b.Encoded, BytesRefComparer.Instance),
                // Azure Search's default: most matches first, ties broken by value so that
                // the order is stable rather than dependent on document order.
                _ => buckets
                    .OrderByDescending(b => b.Count)
                    .ThenBy(b => b.Encoded, BytesRefComparer.Instance),
            };

            IEnumerable<(object? Value, BytesRef Encoded, int Count)> limited = ordered;

            if (facet.Count is { } limit)
            {
                limited = limited.Take(limit);
            }

            return limited
                .Select(b => new FacetBucket { Value = b.Value, Count = b.Count })
                .ToList();
        }

        /// <summary>
        /// Builds contiguous range buckets from the facet's bounds, counting each document's
        /// values into the bucket its value falls in.
        /// </summary>
        /// <remarks>
        /// N bounds produce N+1 buckets: one open below the first bound, one between each
        /// adjacent pair, and one open above the last. Empty buckets are kept — a range facet
        /// describes a fixed scale, and a gap in it is information — which is why these are
        /// built from the bounds rather than from the values seen.
        /// </remarks>
        private IReadOnlyList<FacetBucket> BuildRangeBuckets()
        {
            var elementType = GetElementType(facet.Field);
            var isDate = elementType == "Edm.DateTimeOffset";

            var bounds = facet.Values?.ToList() ?? ComputeIntervalBounds(isDate);

            // An interval facet over no documents has no scale to report: its bounds are
            // derived from the values that matched, and none did. A single unbounded bucket
            // would be meaningless, so the facet comes back empty instead — the same as a
            // value facet that saw nothing. A `values` facet always has bounds, so it keeps
            // its scale here even with nothing in it.
            if (bounds.Count == 0)
            {
                return [];
            }

            var counts = new int[bounds.Count + 1];

            foreach (var values in _rangeDocuments)
            {
                // Each document contributes at most one to any bucket, however many of its
                // values fall there, so the buckets it touches are collected first.
                var touched = new HashSet<int>();

                foreach (var value in values)
                {
                    // The bucket is the first bound the value falls below; a value at or
                    // above every bound lands in the open-ended last bucket.
                    var slot = bounds.FindIndex(b => value < b);
                    touched.Add(slot < 0 ? bounds.Count : slot);
                }

                foreach (var slot in touched)
                {
                    counts[slot]++;
                }
            }

            var buckets = new List<FacetBucket>(counts.Length);

            for (var i = 0; i < counts.Length; i++)
            {
                buckets.Add(new FacetBucket
                {
                    // The outermost buckets are open-ended, so they carry only the bound they
                    // actually have. Azure Search omits the other key entirely.
                    From = i == 0 ? null : ToBoundValue(bounds[i - 1], isDate),
                    To = i == counts.Length - 1 ? null : ToBoundValue(bounds[i], isDate),
                    Count = counts[i],
                });
            }

            return buckets;
        }

        /// <summary>
        /// Derives the bounds of an <c>interval</c> facet from the values actually matched,
        /// since an interval names a bucket width rather than the buckets themselves.
        /// </summary>
        private List<double> ComputeIntervalBounds(bool isDate)
        {
            if (_rangeDocuments.Count == 0)
            {
                return [];
            }

            var min = _rangeDocuments.Min(v => v.Min());
            var max = _rangeDocuments.Max(v => v.Max());

            return isDate
                ? ComputeDateBounds(min, max)
                : ComputeNumericBounds(min, max);
        }

        /// <summary>
        /// Numeric interval bounds, which Azure Search anchors at zero: an interval of 100
        /// produces boundaries at 100, 200, and so on regardless of where the values start.
        /// </summary>
        private List<double> ComputeNumericBounds(double min, double max)
        {
            var interval = facet.NumericInterval!.Value;

            var bounds = new List<double>();

            // Start at the first boundary strictly above the smallest value, so the first
            // bucket holds everything below it, and stop once the largest value is covered.
            var start = Math.Floor(min / interval) * interval;

            for (var bound = start + interval; bound <= max; bound += interval)
            {
                bounds.Add(bound);
            }

            // A value exactly at the top boundary needs a bucket above it to sit in.
            if (bounds.Count == 0 || bounds[^1] <= max)
            {
                bounds.Add(bounds.Count == 0 ? start + interval : bounds[^1] + interval);
            }

            return bounds;
        }

        /// <summary>
        /// Date interval bounds, aligned to the calendar unit in the facet's time zone.
        /// </summary>
        private List<double> ComputeDateBounds(double min, double max)
        {
            var offset = facet.TimeOffset;
            var unit = facet.DateInterval!.Value;

            var first = DateTimeOffset.FromUnixTimeMilliseconds((long)min).ToOffset(offset);
            var last = DateTimeOffset.FromUnixTimeMilliseconds((long)max).ToOffset(offset);

            var bounds = new List<double>();

            var cursor = Advance(Truncate(first, unit, offset), unit);

            // Bounded by the number of buckets a sane request produces; a pathological range
            // (minute intervals over decades) would otherwise run for a very long time.
            const int maxBuckets = 10_000;

            while (cursor <= last && bounds.Count < maxBuckets)
            {
                bounds.Add(cursor.ToUnixTimeMilliseconds());
                cursor = Advance(cursor, unit);
            }

            // As with numbers, the topmost value needs a bucket above it.
            if (bounds.Count < maxBuckets)
            {
                bounds.Add(cursor.ToUnixTimeMilliseconds());
            }

            return bounds;
        }

        /// <summary>
        /// Rounds a timestamp down to the start of the calendar unit containing it.
        /// </summary>
        private static DateTimeOffset Truncate(DateTimeOffset value, DateInterval unit, TimeSpan offset) => unit switch
        {
            DateInterval.Minute => new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, offset),
            DateInterval.Hour => new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, offset),
            DateInterval.Day => new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, offset),
            // Weeks start on Sunday, matching .NET's own DayOfWeek numbering.
            DateInterval.Week => new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, offset)
                .AddDays(-(int)value.DayOfWeek),
            DateInterval.Month => new DateTimeOffset(value.Year, value.Month, 1, 0, 0, 0, offset),
            DateInterval.Quarter => new DateTimeOffset(value.Year, ((value.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, offset),
            DateInterval.Year => new DateTimeOffset(value.Year, 1, 1, 0, 0, 0, offset),
            _ => value,
        };

        private static DateTimeOffset Advance(DateTimeOffset value, DateInterval unit) => unit switch
        {
            DateInterval.Minute => value.AddMinutes(1),
            DateInterval.Hour => value.AddHours(1),
            DateInterval.Day => value.AddDays(1),
            DateInterval.Week => value.AddDays(7),
            DateInterval.Month => value.AddMonths(1),
            DateInterval.Quarter => value.AddMonths(3),
            DateInterval.Year => value.AddYears(1),
            _ => value,
        };

        /// <summary>
        /// Renders a range bound as it appears in the response: a date string for a date
        /// facet, and the number itself otherwise.
        /// </summary>
        private static object ToBoundValue(double bound, bool isDate) => isDate
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)bound)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            : bound;

        private static string GetElementType(SearchField field) => field.IsCollection()
            ? SearchFieldExtensions.GetCollectionElementType(field.Type)
            : field.Type;
    }

    /// <summary>
    /// Orders encoded facet values by their bytes, which <see cref="FacetSupport"/> writes so
    /// that byte order matches the values' own order.
    /// </summary>
    private sealed class BytesRefComparer : IComparer<BytesRef>
    {
        public static readonly BytesRefComparer Instance = new();

        public int Compare(BytesRef? x, BytesRef? y) => x switch
        {
            null when y is null => 0,
            null => -1,
            _ => y is null ? 1 : x.CompareTo(y),
        };
    }
}
