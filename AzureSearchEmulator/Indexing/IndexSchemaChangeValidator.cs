using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks a <c>CreateOrUpdate</c> against the stored index definition, rejecting the field
/// changes real Azure Search refuses (issue #32).
/// </summary>
/// <remarks>
/// Azure Search treats a field's shape as immutable once the index exists: the attributes
/// below all decide how the field was written to the underlying index, so changing one would
/// invalidate documents already indexed under the old shape. Azure's answer is to refuse the
/// update and make the caller delete and recreate the index.
///
/// The emulator accepted these silently, which is worse than a missing feature: code that
/// passed locally failed against the real service, and an indexer looping on a
/// <c>CreateOrUpdate</c> it believed had succeeded never advanced. Refusing here lets that
/// divergence surface in a local test run instead.
///
/// Comparison is on the deserialized <see cref="SearchField"/> rather than the raw JSON so
/// that a property the caller omitted is judged by the same defaults the stored definition
/// was read with — otherwise re-sending an unchanged definition that simply left
/// <c>filterable</c> off would read as a change to it.
/// </remarks>
public static class IndexSchemaChangeValidator
{
    /// <summary>
    /// Returns an error message describing the first disallowed change, or null when the
    /// update only makes changes Azure Search permits.
    /// </summary>
    public static string? FindDisallowedChange(SearchIndex existing, SearchIndex updated)
        => FindDisallowedChange(existing.Fields, updated.Fields, "");

    private static string? FindDisallowedChange(
        IList<SearchField> existingFields,
        IList<SearchField> updatedFields,
        string pathPrefix)
    {
        var updatedByName = updatedFields.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var existingField in existingFields)
        {
            var path = pathPrefix + existingField.Name;

            if (!updatedByName.TryGetValue(existingField.Name, out var updatedField))
            {
                return $"Existing field '{path}' cannot be deleted.";
            }

            if (HasImmutableDifference(existingField, updatedField))
            {
                return $"Existing field '{path}' cannot be changed.";
            }

            // Sub-fields of a complex field are fields in their own right, and Azure applies
            // the same immutability to them.
            var subFieldError = FindDisallowedChange(
                existingField.Fields,
                updatedField.Fields,
                path + ComplexTypeSupport.PathSeparator);

            if (subFieldError != null)
            {
                return subFieldError;
            }
        }

        return null;
    }

    private static bool HasImmutableDifference(SearchField existing, SearchField updated)
        => !string.Equals(existing.Type, updated.Type, StringComparison.Ordinal)
           || existing.Key.GetValueOrDefault() != updated.Key.GetValueOrDefault()
           || existing.Searchable.GetValueOrDefault() != updated.Searchable.GetValueOrDefault()
           || existing.Filterable != updated.Filterable
           || existing.Sortable.GetValueOrDefault() != updated.Sortable.GetValueOrDefault()
           || existing.Facetable.GetValueOrDefault() != updated.Facetable.GetValueOrDefault()
           || existing.Retrievable != updated.Retrievable
           // A vector's length is fixed once documents exist: every stored vector was accepted
           // against the old value, so changing it would leave them disagreeing with the schema
           // and make the field unsearchable against a query vector of either length (issue #46).
           || existing.Dimensions != updated.Dimensions
           || !string.Equals(existing.Analyzer, updated.Analyzer, StringComparison.Ordinal)
           || !string.Equals(existing.SearchAnalyzer, updated.SearchAnalyzer, StringComparison.Ordinal)
           || !string.Equals(existing.IndexAnalyzer, updated.IndexAnalyzer, StringComparison.Ordinal);
}
