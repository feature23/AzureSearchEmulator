using System.Globalization;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// How facet buckets are ordered within a single facet's list.
/// </summary>
public enum FacetSort
{
    /// <summary>
    /// Descending by count, ties broken ascending by value. Azure Search's default.
    /// </summary>
    CountDescending,

    /// <summary>
    /// Ascending by count, ties broken ascending by value (<c>sort:-count</c>).
    /// </summary>
    CountAscending,

    /// <summary>
    /// Ascending by value (<c>sort:value</c>).
    /// </summary>
    ValueAscending,

    /// <summary>
    /// Descending by value (<c>sort:-value</c>).
    /// </summary>
    ValueDescending,
}

/// <summary>
/// One entry of the <c>facet</c> query parameter: the field to bucket by, plus the options
/// controlling how its buckets are built and ordered.
/// </summary>
/// <remarks>
/// A facet expression is a field path followed by comma-separated <c>name:value</c> options,
/// i.e. <c>Category,count:5,sort:-value</c> — see
/// https://learn.microsoft.com/en-us/azure/search/search-faceted-navigation. The options fall
/// into two mutually exclusive groups: <c>count</c>/<c>sort</c> shape a *value* facet, one
/// bucket per distinct value, while <c>values</c>/<c>interval</c> turn the facet into a
/// *range* facet, one bucket per numeric or date span. <c>timeoffset</c> only qualifies
/// <c>interval</c> on a date field.
///
/// Parsing validates against the index rather than deferring to the counting pass, so that a
/// bad expression fails the request outright the way Azure Search does, instead of quietly
/// producing an empty or nonsensical facet.
/// </remarks>
public sealed class FacetRequest
{
    /// <summary>
    /// Azure Search's default cap on the number of buckets returned for a value facet.
    /// </summary>
    private const int DefaultCount = 10;

    /// <summary>
    /// The name the facet is reported under in <c>@search.facets</c>, which is the field path
    /// as the schema spells it rather than as the caller typed it.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The canonical, slash-delimited path of the field being faceted.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The field being faceted. For a collection this is the collection field itself; its
    /// elements are what get counted.
    /// </summary>
    public required SearchField Field { get; init; }

    /// <summary>
    /// Maximum number of buckets to return, or null for all of them (<c>count:0</c>).
    /// Ignored by range facets, which return exactly the buckets their bounds define.
    /// </summary>
    public int? Count { get; init; } = DefaultCount;

    public FacetSort Sort { get; init; } = FacetSort.CountDescending;

    /// <summary>
    /// Explicit bucket boundaries from <c>values</c>, in ascending order, or null when this
    /// is not a <c>values</c> facet. Doubles for numeric fields; Unix-epoch milliseconds for
    /// date fields, matching how both are stored.
    /// </summary>
    public IReadOnlyList<double>? Values { get; init; }

    /// <summary>
    /// Bucket width from <c>interval</c> on a numeric field, or null when this is not a
    /// numeric interval facet.
    /// </summary>
    public double? NumericInterval { get; init; }

    /// <summary>
    /// Calendar unit from <c>interval</c> on a date field, or null when this is not a date
    /// interval facet.
    /// </summary>
    public DateInterval? DateInterval { get; init; }

    /// <summary>
    /// The offset from UTC that date interval boundaries are aligned to (<c>timeoffset</c>),
    /// defaulting to UTC itself.
    /// </summary>
    public TimeSpan TimeOffset { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// True when this facet produces range buckets — with <c>from</c>/<c>to</c> bounds —
    /// rather than one bucket per distinct value.
    /// </summary>
    public bool IsRange => Values != null || NumericInterval != null || DateInterval != null;

    /// <summary>
    /// Parses every facet expression in a request, or returns null when none were given.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An expression names an unknown or non-facetable field, or combines options that Azure
    /// Search does not allow together.
    /// </exception>
    public static IReadOnlyList<FacetRequest>? Parse(SearchIndex index, IList<string>? facets)
    {
        if (facets == null || facets.Count == 0)
        {
            return null;
        }

        var parsed = facets
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => ParseOne(index, f))
            .ToList();

        return parsed.Count == 0 ? null : parsed;
    }

    private static FacetRequest ParseOne(SearchIndex index, string expression)
    {
        var parts = expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            throw new InvalidOperationException("A facet expression must name a field.");
        }

        var path = parts[0];

        if (!ComplexTypeSupport.TryResolvePath(index, path, out var field, out var canonicalPath))
        {
            throw new InvalidOperationException(
                $"Unable to find field '{path}' in the index '{index.Name}'");
        }

        if (!field.Facetable.GetValueOrDefault())
        {
            throw new InvalidOperationException(
                $"The field '{canonicalPath}' in the index '{index.Name}' is not facetable.");
        }

        // Azure Search rejects faceting on a complex field itself: a complex value is an
        // object rather than a countable value. Its sub-fields are faceted individually
        // instead, which is why the path is resolved before this check.
        if (field.IsComplex() || field.IsComplexCollection())
        {
            throw new InvalidOperationException(
                $"The field '{canonicalPath}' in the index '{index.Name}' is a complex field, which cannot be faceted.");
        }

        var elementType = field.IsCollection()
            ? SearchFieldExtensions.GetCollectionElementType(field.Type)
            : field.Type;

        // Geography is rejected too, in either its scalar or collection form: coordinates are
        // effectively unique per document, so counting them yields nothing useful.
        if (elementType == GeoSupport.GeographyPointType)
        {
            throw new InvalidOperationException(
                $"The field '{canonicalPath}' in the index '{index.Name}' is a geography field, which cannot be faceted.");
        }

        int? count = DefaultCount;
        var sort = FacetSort.CountDescending;
        IReadOnlyList<double>? values = null;
        double? numericInterval = null;
        DateInterval? dateInterval = null;
        TimeSpan? timeOffset = null;
        var sawCountOrSort = false;

        foreach (var part in parts.Skip(1))
        {
            var separator = part.IndexOf(':');

            if (separator <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid facet option '{part}' in facet expression '{expression}'; expected 'name:value'.");
            }

            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();

            switch (name.ToLowerInvariant())
            {
                case "count":
                    count = ParseCount(value, expression);
                    sawCountOrSort = true;
                    break;

                case "sort":
                    sort = ParseSort(value, expression);
                    sawCountOrSort = true;
                    break;

                case "values":
                    values = ParseValues(value, elementType, canonicalPath, expression);
                    break;

                case "interval":
                    (numericInterval, dateInterval) = ParseInterval(value, elementType, canonicalPath, expression);
                    break;

                case "timeoffset":
                    timeOffset = ParseTimeOffset(value, expression);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown facet option '{name}' in facet expression '{expression}'.");
            }
        }

        var isRange = values != null || numericInterval != null || dateInterval != null;

        if (values != null && (numericInterval != null || dateInterval != null))
        {
            throw new InvalidOperationException(
                $"The facet options 'values' and 'interval' cannot be combined, in facet expression '{expression}'.");
        }

        if (isRange && sawCountOrSort)
        {
            throw new InvalidOperationException(
                $"The facet options 'count' and 'sort' cannot be combined with 'values' or 'interval', in facet expression '{expression}'.");
        }

        if (timeOffset != null && dateInterval == null)
        {
            throw new InvalidOperationException(
                $"The facet option 'timeoffset' is only valid with 'interval' on an {DateType} field, in facet expression '{expression}'.");
        }

        return new FacetRequest
        {
            Name = canonicalPath,
            Path = canonicalPath,
            Field = field,
            Count = count,
            Sort = sort,
            Values = values,
            NumericInterval = numericInterval,
            DateInterval = dateInterval,
            TimeOffset = timeOffset ?? TimeSpan.Zero,
        };
    }

    private const string DateType = "Edm.DateTimeOffset";

    private static int? ParseCount(string value, string expression)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
        {
            throw new InvalidOperationException(
                $"Invalid facet count '{value}' in facet expression '{expression}'; expected a non-negative integer.");
        }

        // Azure Search treats count:0 as "no limit" rather than "no buckets".
        return count == 0 ? null : count;
    }

    private static FacetSort ParseSort(string value, string expression) => value.ToLowerInvariant() switch
    {
        "count" => FacetSort.CountDescending,
        "-count" => FacetSort.CountAscending,
        "value" => FacetSort.ValueAscending,
        "-value" => FacetSort.ValueDescending,
        _ => throw new InvalidOperationException(
            $"Invalid facet sort '{value}' in facet expression '{expression}'; expected count, -count, value, or -value."),
    };

    /// <summary>
    /// Parses the pipe-delimited bounds of a <c>values</c> facet into the same numeric space
    /// the field is stored in, so that counting is a plain comparison.
    /// </summary>
    private static IReadOnlyList<double> ParseValues(
        string value,
        string elementType,
        string path,
        string expression)
    {
        var bounds = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (bounds.Length == 0)
        {
            throw new InvalidOperationException(
                $"The facet option 'values' requires at least one bound, in facet expression '{expression}'.");
        }

        var parsed = bounds.Select(b => ParseBound(b, elementType, path, expression)).ToList();

        // Azure Search documents that bounds must be given in ascending order; out-of-order
        // bounds would otherwise silently produce empty buckets.
        for (var i = 1; i < parsed.Count; i++)
        {
            if (parsed[i] <= parsed[i - 1])
            {
                throw new InvalidOperationException(
                    $"The facet option 'values' requires bounds in ascending order, in facet expression '{expression}'.");
            }
        }

        return parsed;
    }

    private static double ParseBound(string bound, string elementType, string path, string expression)
    {
        if (elementType == DateType)
        {
            if (!DateTimeOffset.TryParse(bound, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                throw new InvalidOperationException(
                    $"Invalid facet value '{bound}' for the {DateType} field '{path}', in facet expression '{expression}'.");
            }

            return date.ToUnixTimeMilliseconds();
        }

        if (!IsNumeric(elementType))
        {
            throw new InvalidOperationException(
                $"The facet options 'values' and 'interval' require a numeric or {DateType} field, but '{path}' is {elementType}.");
        }

        if (!double.TryParse(bound, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException(
                $"Invalid facet value '{bound}' for the numeric field '{path}', in facet expression '{expression}'.");
        }

        return number;
    }

    private static (double? Numeric, DateInterval? Date) ParseInterval(
        string value,
        string elementType,
        string path,
        string expression)
    {
        if (elementType == DateType)
        {
            var unit = value.ToLowerInvariant() switch
            {
                "minute" => Searching.DateInterval.Minute,
                "hour" => Searching.DateInterval.Hour,
                "day" => Searching.DateInterval.Day,
                "week" => Searching.DateInterval.Week,
                "month" => Searching.DateInterval.Month,
                "quarter" => Searching.DateInterval.Quarter,
                "year" => Searching.DateInterval.Year,
                _ => throw new InvalidOperationException(
                    $"Invalid facet interval '{value}' for the {DateType} field '{path}'; expected minute, hour, day, week, month, quarter, or year."),
            };

            return (null, unit);
        }

        if (!IsNumeric(elementType))
        {
            throw new InvalidOperationException(
                $"The facet options 'values' and 'interval' require a numeric or {DateType} field, but '{path}' is {elementType}.");
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval) || interval <= 0)
        {
            throw new InvalidOperationException(
                $"Invalid facet interval '{value}' in facet expression '{expression}'; expected a number greater than zero.");
        }

        return (interval, null);
    }

    /// <summary>
    /// Parses a <c>timeoffset</c> in any of the forms Azure Search accepts: <c>[+-]hh:mm</c>,
    /// <c>[+-]hhmm</c>, or <c>[+-]hh</c>.
    /// </summary>
    private static TimeSpan ParseTimeOffset(string value, string expression)
    {
        var sign = 1;
        var rest = value;

        if (rest.StartsWith('+') || rest.StartsWith('-'))
        {
            sign = rest[0] == '-' ? -1 : 1;
            rest = rest[1..];
        }

        int hours;
        var minutes = 0;

        if (rest.Contains(':'))
        {
            var parts = rest.Split(':');

            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
            {
                throw new InvalidOperationException(
                    $"Invalid facet timeoffset '{value}' in facet expression '{expression}'.");
            }
        }
        else if (rest.Length == 4)
        {
            if (!int.TryParse(rest[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours)
                || !int.TryParse(rest[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes))
            {
                throw new InvalidOperationException(
                    $"Invalid facet timeoffset '{value}' in facet expression '{expression}'.");
            }
        }
        else if (!int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
        {
            throw new InvalidOperationException(
                $"Invalid facet timeoffset '{value}' in facet expression '{expression}'.");
        }

        if (hours is < 0 or > 14 || minutes is < 0 or > 59)
        {
            throw new InvalidOperationException(
                $"Invalid facet timeoffset '{value}' in facet expression '{expression}'; the offset is out of range.");
        }

        return new TimeSpan(sign * hours, sign * minutes, 0);
    }

    private static bool IsNumeric(string type) =>
        type is "Edm.Int32" or "Edm.Int64" or "Edm.Double";
}

/// <summary>
/// The calendar unit an <c>interval</c> facet buckets an <c>Edm.DateTimeOffset</c> field by.
/// </summary>
public enum DateInterval
{
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Quarter,
    Year,
}
