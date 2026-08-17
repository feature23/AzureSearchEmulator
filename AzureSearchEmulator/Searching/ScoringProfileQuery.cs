using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Queries;
using Lucene.Net.Search;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Applies a scoring profile's functions to the score of whatever the inner query matched
/// (issue #47).
/// </summary>
/// <remarks>
/// The functions do not change which documents match — only how they rank — so this wraps the
/// query rather than joining it. <see cref="CustomScoreQuery"/> is exactly that shape: it keeps
/// the inner query's matches and hands each one's score to <see cref="CustomScoreProvider"/> to
/// be adjusted.
///
/// The per-field text weights of a profile are <em>not</em> applied here; they belong to the
/// query parse, where they can weight each field's contribution to the text match. See
/// <see cref="LuceneNetIndexSearcher"/>.
/// </remarks>
public class ScoringProfileQuery : CustomScoreQuery
{
    private readonly ScoringProfile _profile;
    private readonly IReadOnlyList<PreparedFunction> _functions;

    private ScoringProfileQuery(Query subQuery, ScoringProfile profile, IReadOnlyList<PreparedFunction> functions)
        : base(subQuery)
    {
        _profile = profile;
        _functions = functions;
    }

    /// <summary>
    /// Wraps a query so the profile's functions boost its results, or returns it unchanged when
    /// the profile has no function that can apply.
    /// </summary>
    /// <remarks>
    /// A profile carrying only text weights needs no wrapper at all, and neither does one whose
    /// every function is missing the scoring parameter it depends on.
    /// </remarks>
    public static Query Wrap(
        Query subQuery,
        SearchIndex index,
        ScoringProfile profile,
        ScoringParameterCollection parameters,
        DateTimeOffset now)
    {
        var prepared = new List<PreparedFunction>();

        foreach (var function in profile.Functions)
        {
            if (!ComplexTypeSupport.TryResolvePath(index, function.FieldName, out var field, out var path))
            {
                // Refused when the index is defined, so a field that cannot be resolved here
                // belongs to a definition written before that check existed.
                continue;
            }

            if (TryPrepare(function, field, path, parameters, now) is { } ready)
            {
                prepared.Add(ready);
            }
        }

        return prepared.Count == 0 ? subQuery : new ScoringProfileQuery(subQuery, profile, prepared);
    }

    /// <summary>
    /// Binds a function to the field path it reads and to the query's scoring parameters,
    /// returning null when the request did not supply a parameter the function needs.
    /// </summary>
    /// <remarks>
    /// Azure refuses a query that omits a required scoring parameter, which
    /// <see cref="ScoringProfileSupport"/> checks before the search runs; by the time a function
    /// is prepared here the parameters it needs are known to be present.
    /// </remarks>
    private static PreparedFunction? TryPrepare(
        ScoringFunction function,
        SearchField field,
        string path,
        ScoringParameterCollection parameters,
        DateTimeOffset now)
    {
        switch (function)
        {
            case MagnitudeScoringFunction magnitude:
                return new PreparedFunction(path, reader => ReadNumeric(reader, path, field),
                    (value, _) => ScoringFunctionEvaluator.GetMagnitudeBoost(magnitude, value));

            case FreshnessScoringFunction freshness:
                return new PreparedFunction(path, reader => ReadNumeric(reader, path, field),
                    (value, _) => ScoringFunctionEvaluator.GetFreshnessBoost(
                        freshness,
                        // Dates are indexed as Unix milliseconds; see SearchFieldExtensions.
                        DateTimeOffset.FromUnixTimeMilliseconds((long)value),
                        now));

            case DistanceScoringFunction distance:
            {
                var origin = parameters.GetReferencePoint(distance.Distance!.ReferencePointParameter);

                if (origin == null)
                {
                    return null;
                }

                return new PreparedFunction(path,
                    reader => ReadDistance(reader, path, origin.Value),
                    (value, _) => ScoringFunctionEvaluator.GetDistanceBoost(distance, value));
            }

            case TagScoringFunction tag:
            {
                var tags = parameters.GetValues(tag.Tag!.TagsParameter);

                if (tags == null || tags.Count == 0)
                {
                    return null;
                }

                return new PreparedFunction(path,
                    reader => ReadTagMatches(reader, path, tags),
                    (matched, _) => ScoringFunctionEvaluator.GetTagBoost(tag, (int)matched, tags.Count));
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Reads a numeric or date field, which the emulator indexes as a single numeric value per
    /// document.
    /// </summary>
    /// <remarks>
    /// The field cache exposes the value as a double regardless of the underlying numeric type,
    /// which is what the magnitude and freshness curves want. Documents with no value report
    /// <see cref="double.NaN"/> so the caller can tell them from a genuine zero.
    /// </remarks>
    private static Func<int, double> ReadNumeric(AtomicReader reader, string path, SearchField field)
    {
        var hasValue = FieldCache.DEFAULT.GetDocsWithField(reader, path);

        // The field cache is typed, and asking for the wrong width returns nothing rather than
        // converting, so each indexed type is read as itself.
        if (field.Type == "Edm.Int32")
        {
            var values = FieldCache.DEFAULT.GetInt32s(reader, path, false);
            return doc => hasValue.Get(doc) ? values.Get(doc) : double.NaN;
        }

        if (field.Type is "Edm.Int64" or "Edm.DateTimeOffset")
        {
            var values = FieldCache.DEFAULT.GetInt64s(reader, path, false);
            return doc => hasValue.Get(doc) ? values.Get(doc) : double.NaN;
        }

        var doubles = FieldCache.DEFAULT.GetDoubles(reader, path, false);
        return doc => hasValue.Get(doc) ? doubles.Get(doc) : double.NaN;
    }

    /// <summary>
    /// Reads the distance in kilometers from the reference point to a document's nearest point.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="GeoSupport.GetPointReader"/> rather than the latitude/longitude
    /// field caches so that a <c>Collection(Edm.GeographyPoint)</c> is handled too: a document
    /// with several points is scored by the nearest, which is the one that would satisfy a
    /// proximity filter.
    /// </remarks>
    private static Func<int, double> ReadDistance(
        AtomicReader reader,
        string path,
        (double Lon, double Lat) origin)
    {
        var points = GeoSupport.GetPointReader(reader, path);

        return doc =>
        {
            var docPoints = points(doc);

            if (docPoints.Count == 0)
            {
                return double.NaN;
            }

            var nearest = double.MaxValue;

            foreach (var (lon, lat) in docPoints)
            {
                nearest = Math.Min(nearest, GeoSupport.GetDistanceKm(lon, lat, origin.Lon, origin.Lat));
            }

            return nearest;
        };
    }

    /// <summary>
    /// Counts how many of the caller's tags a document carries.
    /// </summary>
    /// <remarks>
    /// Matched against the un-analyzed sidecar copy of the field, so a tag is compared as the
    /// whole value it is rather than as the tokens an analyzer would split it into — "New York"
    /// stays one tag. The document's terms are collected once per segment and intersected with
    /// the requested tags, rather than running a term lookup per tag per document.
    ///
    /// Comparison is ordinal: Azure gives no guidance here, and an exact match is the reading
    /// that cannot silently boost a document whose tag merely resembles the one asked for.
    /// </remarks>
    private static Func<int, double> ReadTagMatches(
        AtomicReader reader,
        string path,
        IReadOnlyList<string> tags)
    {
        // A searchable field's own name holds analyzed tokens, so the exact-value copy written
        // for filtering is the one to match against.
        var rawField = SearchFieldExtensions.GetRawStringFieldName(path);

        var matchesByDoc = new Dictionary<int, int>();

        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var docs = reader.GetTermDocsEnum(new Term(rawField, new BytesRef(tag)));

            if (docs == null)
            {
                continue;
            }

            for (var doc = docs.NextDoc(); doc != DocIdSetIterator.NO_MORE_DOCS; doc = docs.NextDoc())
            {
                matchesByDoc[doc] = matchesByDoc.GetValueOrDefault(doc) + 1;
            }
        }

        return doc => matchesByDoc.GetValueOrDefault(doc);
    }

    protected override CustomScoreProvider GetCustomScoreProvider(AtomicReaderContext context)
        => new Provider(context, _profile, _functions);

    /// <summary>
    /// A function bound to one field, with the readers and curve needed to score a document.
    /// </summary>
    /// <param name="Path">The indexed path of the field the function reads.</param>
    /// <param name="ReaderFactory">Builds the per-segment accessor for the field's value.</param>
    /// <param name="GetBoost">
    /// Turns a document's value into a boost, or null when the function does not apply to it.
    /// </param>
    private sealed record PreparedFunction(
        string Path,
        Func<AtomicReader, Func<int, double>> ReaderFactory,
        Func<double, int, double?> GetBoost);

    private sealed class Provider : CustomScoreProvider
    {
        private readonly ScoringProfile _profile;
        private readonly IReadOnlyList<PreparedFunction> _functions;
        private readonly List<Func<int, double>> _readers;

        public Provider(
            AtomicReaderContext context,
            ScoringProfile profile,
            IReadOnlyList<PreparedFunction> functions)
            : base(context)
        {
            _profile = profile;
            _functions = functions;

            // Bound once per segment rather than per document, which is what the field caches
            // and doc values expect.
            _readers = functions.Select(i => i.ReaderFactory(context.AtomicReader)).ToList();
        }

        public override float CustomScore(int doc, float subQueryScore, float[] valSrcScores)
            => CustomScore(doc, subQueryScore, 0f);

        public override float CustomScore(int doc, float subQueryScore, float valSrcScore)
        {
            var boosts = new List<double>(_functions.Count);

            for (var i = 0; i < _functions.Count; i++)
            {
                var value = _readers[i](doc);

                // NaN marks a document with no value for the field, which no function applies
                // to. Azure boosts on the strength of a value; absence is not a weak value.
                if (double.IsNaN(value))
                {
                    continue;
                }

                if (_functions[i].GetBoost(value, doc) is { } boost)
                {
                    boosts.Add(boost);
                }
            }

            var aggregate = ScoringFunctionEvaluator.Aggregate(boosts, _profile.FunctionAggregation);

            return (float)(subQueryScore * aggregate);
        }
    }
}
