using System.Globalization;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// The <c>scoringParameter</c> values of a request, parsed into the named values a scoring
/// profile's functions look up (issue #47).
/// </summary>
/// <remarks>
/// Azure passes these as flat strings in the form <c>name-value</c>, with several values
/// separated by commas: <c>mytags-luxury,budget</c>, or <c>mylocation--122.2,44.8</c> for a
/// reference point.
///
/// <para>That second example is the one to be careful with. The doubled dash is not a delimiter:
/// it is the single dash separating name from value, followed by a longitude that happens to be
/// negative. Splitting on <c>--</c> would work for that example and then fail on any reference
/// point east of Greenwich, where <c>berlin-13.4,52.5</c> has just one dash — a bug that would
/// only ever show up for some coordinates. The name is therefore taken as everything before the
/// <em>first</em> dash.</para>
/// </remarks>
public class ScoringParameterCollection
{
    private readonly Dictionary<string, IReadOnlyList<string>> _parameters;

    private ScoringParameterCollection(Dictionary<string, IReadOnlyList<string>> parameters)
    {
        _parameters = parameters;
    }

    public static ScoringParameterCollection Empty { get; } =
        new(new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the request's raw <c>scoringParameter</c> strings.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A parameter with no dash at all, which cannot name anything.
    /// </exception>
    public static ScoringParameterCollection Parse(IEnumerable<string>? values)
    {
        var parameters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var separator = value.IndexOf('-');

            if (separator <= 0)
            {
                throw new InvalidOperationException(
                    $"The scoring parameter '{value}' is not in the required 'name-value' format, " +
                    "for example 'mytags-luxury,budget' or 'mylocation--122.2,44.8'.");
            }

            var name = value[..separator];

            // Kept as raw strings: only the function that consumes a parameter knows whether it
            // wants a list of tags or a pair of coordinates.
            parameters[name] = value[(separator + 1)..]
                .Split(',', StringSplitOptions.TrimEntries)
                .ToList();
        }

        return new ScoringParameterCollection(parameters);
    }

    /// <summary>
    /// The values supplied for a parameter, or null when the request did not supply it.
    /// </summary>
    public IReadOnlyList<string>? GetValues(string name)
        => _parameters.TryGetValue(name, out var values) ? values : null;

    /// <summary>
    /// Reads a parameter as the longitude/latitude reference point of a <c>distance</c>
    /// function.
    /// </summary>
    /// <remarks>
    /// Azure orders the coordinates longitude first, matching the GeoJSON and WKT forms the
    /// emulator already uses elsewhere — see <see cref="GeoSupport"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The parameter is present but is not a pair of finite numbers.
    /// </exception>
    public (double Lon, double Lat)? GetReferencePoint(string name)
    {
        var values = GetValues(name);

        if (values == null)
        {
            return null;
        }

        if (values.Count != 2
            || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.IsFinite(lon)
            || !double.IsFinite(lat))
        {
            throw new InvalidOperationException(
                $"The scoring parameter '{name}' must be a longitude and latitude, " +
                $"for example '{name}--122.2,44.8'.");
        }

        return (lon, lat);
    }
}
