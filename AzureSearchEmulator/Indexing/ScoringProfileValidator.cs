using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the scoring profiles of an index definition against the fields they name (issue #47).
/// </summary>
/// <remarks>
/// Azure validates a scoring profile when the index is created rather than when a query uses
/// it, and refuses a profile whose function targets a field that cannot support it — a
/// <c>freshness</c> function over a string, say. Validating here rather than at query time
/// matches that, and is also the more useful place: a profile that names a mistyped field is a
/// definition error, and reporting it at definition time points at the mistake instead of
/// leaving it to surface as an unexplained absence of boosting later.
///
/// The checks are deliberately confined to what is genuinely unworkable. A weight on a
/// non-searchable field, for example, is refused because the field contributes nothing to a
/// text match and the weight could never do anything.
/// </remarks>
public static class ScoringProfileValidator
{
    /// <summary>
    /// Numeric types a <c>magnitude</c> function can read.
    /// </summary>
    private static readonly HashSet<string> MagnitudeTypes =
        new(StringComparer.Ordinal) { "Edm.Int32", "Edm.Int64", "Edm.Double" };

    /// <summary>
    /// The largest boost a function may declare.
    /// </summary>
    /// <remarks>
    /// Azure documents no ceiling, so this is the emulator's own. It is set well above any
    /// useful relevance tuning — a boost of a thousand already swamps every text score — while
    /// staying far enough below <see cref="float.MaxValue"/> that even the product of a
    /// profile's full complement of functions cannot overflow the float a score is returned as.
    /// </remarks>
    public const double MaxBoost = 1_000_000;

    /// <summary>
    /// Returns a message describing the first invalid profile, or null when every profile is
    /// usable against these fields.
    /// </summary>
    public static string? FindInvalidProfile(SearchIndex index)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in index.ScoringProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                return "A scoring profile must have a name.";
            }

            // Azure matches a profile name case-insensitively, so two profiles differing only
            // in case would make a request naming either one ambiguous.
            if (!names.Add(profile.Name))
            {
                return $"The index has more than one scoring profile named '{profile.Name}'.";
            }

            if (FindInvalidProfile(index, profile) is { } error)
            {
                return error;
            }
        }

        // A default naming a profile that does not exist would silently score nothing.
        if (!string.IsNullOrEmpty(index.DefaultScoringProfile)
            && index.FindScoringProfile(index.DefaultScoringProfile) == null)
        {
            return $"The index's defaultScoringProfile '{index.DefaultScoringProfile}' " +
                   "does not match any scoring profile defined on the index.";
        }

        return null;
    }

    private static string? FindInvalidProfile(SearchIndex index, ScoringProfile profile)
    {
        foreach (var (fieldName, weight) in profile.Text?.Weights ?? [])
        {
            var field = ComplexTypeSupport.FindFieldByPath(index, fieldName);

            if (field == null)
            {
                return $"Scoring profile '{profile.Name}' gives a weight to field '{fieldName}', " +
                       "which does not exist in the index.";
            }

            if (!field.Searchable.GetValueOrDefault())
            {
                return $"Scoring profile '{profile.Name}' gives a weight to field '{fieldName}', " +
                       "which is not searchable. Only searchable fields contribute to the text score.";
            }

            // A negative weight has no meaning as a multiplier on a term's contribution, and
            // Azure requires weights to be positive.
            if (weight <= 0 || !double.IsFinite(weight))
            {
                return $"Scoring profile '{profile.Name}' gives field '{fieldName}' a weight of " +
                       $"{weight}, which must be a positive number.";
            }
        }

        foreach (var function in profile.Functions)
        {
            if (FindInvalidFunction(index, profile, function) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private static string? FindInvalidFunction(SearchIndex index, ScoringProfile profile, ScoringFunction function)
    {
        var field = ComplexTypeSupport.FindFieldByPath(index, function.FieldName);

        if (field == null)
        {
            return $"Scoring profile '{profile.Name}' has a {function.Type} function over field " +
                   $"'{function.FieldName}', which does not exist in the index.";
        }

        // Azure defines boost as "a positive number not equal to 1.0", and both bounds matter
        // here. A boost below 1 inverts every curve — the multiplier would then climb from the
        // near end of the range toward 1 at the far end, demoting the documents the function
        // exists to promote, and constantBoostBeyondRange would hold that demotion past the
        // range. An unbounded one overflows: the boosts are aggregated as doubles and the score
        // is a float, so a large enough boost — or a product of several — reaches infinity,
        // which leaves every document mutually unorderable and serializes as a value that is
        // not legal JSON.
        if (!double.IsFinite(function.Boost) || function.Boost <= 1 || function.Boost > MaxBoost)
        {
            return $"Scoring profile '{profile.Name}' has a {function.Type} function over field " +
                   $"'{function.FieldName}' with a boost of {function.Boost}, which must be greater " +
                   $"than 1 and no more than {MaxBoost}.";
        }

        // Azure requires every function's field to be filterable, and the emulator needs it for
        // the same practical reason: the un-analyzed and doc-values copies a function reads at
        // query time are only written for a filterable field.
        if (!field.Filterable)
        {
            return $"Scoring profile '{profile.Name}' has a {function.Type} function over field " +
                   $"'{function.FieldName}', which must be filterable. Scoring functions can only " +
                   "be applied to filterable fields.";
        }

        // Quadratic and logarithmic describe a curve across a range of values, and a tag
        // function has no such range — a tag either matches or does not. Azure rejects the
        // combination rather than picking one of the two ends.
        if (function is TagScoringFunction
            && function.Interpolation is ScoringFunctionInterpolation.Quadratic
                or ScoringFunctionInterpolation.Logarithmic)
        {
            return $"Scoring profile '{profile.Name}' has a tag function over field " +
                   $"'{function.FieldName}' with {function.Interpolation.ToString().ToLowerInvariant()} " +
                   "interpolation, which is not allowed for tag functions.";
        }

        return function switch
        {
            MagnitudeScoringFunction magnitude => ValidateMagnitude(profile, magnitude, field),
            FreshnessScoringFunction freshness => ValidateFreshness(profile, freshness, field),
            DistanceScoringFunction distance => ValidateDistance(profile, distance, field),
            TagScoringFunction tag => ValidateTag(profile, tag, field),
            _ => $"Scoring profile '{profile.Name}' has a function of unsupported type '{function.Type}'."
        };
    }

    private static string? ValidateMagnitude(
        ScoringProfile profile,
        MagnitudeScoringFunction function,
        SearchField field)
    {
        if (function.Magnitude == null)
        {
            return MissingParameters(profile, function, "magnitude");
        }

        var type = field.IsCollection() ? SearchFieldExtensions.GetCollectionElementType(field.Type) : field.Type;

        if (!MagnitudeTypes.Contains(type))
        {
            return WrongType(profile, function, field, "a numeric field (Edm.Int32, Edm.Int64 or Edm.Double)");
        }

        if (!double.IsFinite(function.Magnitude.BoostingRangeStart)
            || !double.IsFinite(function.Magnitude.BoostingRangeEnd))
        {
            return $"Scoring profile '{profile.Name}' has a magnitude function over field " +
                   $"'{function.FieldName}' with a non-finite boosting range.";
        }

        // A start equal to the end leaves no range to interpolate across, which would divide
        // by zero. Start greater than end is allowed: it reverses the direction, boosting
        // smaller values, which Azure supports.
        if (function.Magnitude.BoostingRangeStart == function.Magnitude.BoostingRangeEnd)
        {
            return $"Scoring profile '{profile.Name}' has a magnitude function over field " +
                   $"'{function.FieldName}' whose boostingRangeStart and boostingRangeEnd are both " +
                   $"{function.Magnitude.BoostingRangeStart}; they must differ.";
        }

        return null;
    }

    private static string? ValidateFreshness(
        ScoringProfile profile,
        FreshnessScoringFunction function,
        SearchField field)
    {
        if (function.Freshness == null)
        {
            return MissingParameters(profile, function, "freshness");
        }

        if (field.Type != "Edm.DateTimeOffset")
        {
            return WrongType(profile, function, field, "an Edm.DateTimeOffset field");
        }

        if (string.IsNullOrEmpty(function.Freshness.BoostingDuration))
        {
            return $"Scoring profile '{profile.Name}' has a freshness function over field " +
                   $"'{function.FieldName}' with no boostingDuration.";
        }

        TimeSpan duration;

        try
        {
            duration = ScoringProfileJson.ParseDuration(function.Freshness.BoostingDuration);
        }
        catch (FormatException ex)
        {
            return $"Scoring profile '{profile.Name}' has a freshness function over field " +
                   $"'{function.FieldName}': {ex.Message}";
        }

        // Zero leaves no interval to decay across. A negative duration is legal and boosts
        // older documents instead, so only zero is refused.
        if (duration == TimeSpan.Zero)
        {
            return $"Scoring profile '{profile.Name}' has a freshness function over field " +
                   $"'{function.FieldName}' with a zero boostingDuration.";
        }

        return null;
    }

    private static string? ValidateDistance(
        ScoringProfile profile,
        DistanceScoringFunction function,
        SearchField field)
    {
        if (function.Distance == null)
        {
            return MissingParameters(profile, function, "distance");
        }

        var type = field.IsCollection() ? SearchFieldExtensions.GetCollectionElementType(field.Type) : field.Type;

        if (type != GeoSupport.GeographyPointType)
        {
            return WrongType(profile, function, field, $"a {GeoSupport.GeographyPointType} field");
        }

        if (string.IsNullOrWhiteSpace(function.Distance.ReferencePointParameter))
        {
            return $"Scoring profile '{profile.Name}' has a distance function over field " +
                   $"'{function.FieldName}' with no referencePointParameter.";
        }

        if (function.Distance.BoostingDistance <= 0 || !double.IsFinite(function.Distance.BoostingDistance))
        {
            return $"Scoring profile '{profile.Name}' has a distance function over field " +
                   $"'{function.FieldName}' with a boostingDistance of " +
                   $"{function.Distance.BoostingDistance}, which must be a positive number of kilometers.";
        }

        return null;
    }

    private static string? ValidateTag(ScoringProfile profile, TagScoringFunction function, SearchField field)
    {
        if (function.Tag == null)
        {
            return MissingParameters(profile, function, "tag");
        }

        var type = field.IsCollection() ? SearchFieldExtensions.GetCollectionElementType(field.Type) : field.Type;

        if (type != "Edm.String")
        {
            return WrongType(profile, function, field, "an Edm.String or Collection(Edm.String) field");
        }

        if (string.IsNullOrWhiteSpace(function.Tag.TagsParameter))
        {
            return $"Scoring profile '{profile.Name}' has a tag function over field " +
                   $"'{function.FieldName}' with no tagsParameter.";
        }

        return null;
    }

    private static string MissingParameters(ScoringProfile profile, ScoringFunction function, string property)
        => $"Scoring profile '{profile.Name}' has a {function.Type} function over field " +
           $"'{function.FieldName}' with no '{property}' parameters object.";

    private static string WrongType(
        ScoringProfile profile,
        ScoringFunction function,
        SearchField field,
        string expected)
        => $"Scoring profile '{profile.Name}' has a {function.Type} function over field " +
           $"'{function.FieldName}', which is {field.Type}. A {function.Type} function requires {expected}.";
}
