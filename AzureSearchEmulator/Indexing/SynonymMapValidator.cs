using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks synonym map definitions and the names an index's fields refer to (issue #69).
/// </summary>
/// <remarks>
/// Split across two entry points because the two halves are checked at different times: a map
/// is validated on its own routes when it is written, while the field references are validated
/// with the index that makes them. Following <see cref="NormalizerValidator"/>, both report the
/// mistake when the definition is written rather than leaving it to surface against whichever
/// query first reached the field.
/// </remarks>
public static class SynonymMapValidator
{
    /// <summary>
    /// Returns a message describing the first problem with the map, or null when it is usable.
    /// </summary>
    public static string? FindInvalidSynonymMap(SynonymMap synonymMap)
    {
        if (string.IsNullOrWhiteSpace(synonymMap.Name))
        {
            return "A synonym map must have a name.";
        }

        if (!string.Equals(synonymMap.Format, SynonymMap.SolrFormat, StringComparison.OrdinalIgnoreCase))
        {
            return $"Synonym map '{synonymMap.Name}' declares format '{synonymMap.Format}'; only " +
                   $"'{SynonymMap.SolrFormat}' is supported.";
        }

        // Azure requires the rules, and an empty map would expand nothing while looking to a
        // caller as though it had been applied.
        if (string.IsNullOrWhiteSpace(synonymMap.Synonyms))
        {
            return $"Synonym map '{synonymMap.Name}' must define at least one synonym rule.";
        }

        try
        {
            SynonymMapBuilder.Build(synonymMap);
        }
        catch (SynonymMapDefinitionException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>
    /// Returns a message describing the first field that misuses a synonym map, or null when
    /// the index's references are acceptable.
    /// </summary>
    /// <remarks>
    /// <paramref name="existing"/> is the set of map names the service currently holds. It is
    /// passed in rather than looked up here so this stays a pure function, in keeping with the
    /// other validators the controller chains together.
    /// </remarks>
    public static string? FindInvalidFieldReference(SearchIndex index, IReadOnlySet<string> existing)
    {
        foreach (var (field, path) in EnumerateFields(index.Fields))
        {
            if (field.SynonymMaps.Count == 0)
            {
                continue;
            }

            if (!IsStringField(field))
            {
                return $"Field '{path}' is type {field.Type} and cannot have a synonym map; " +
                       "synonym maps apply to Edm.String and Collection(Edm.String) fields.";
            }

            // Synonyms only ever widen a full-text query, so a field that no query can reach
            // would name a map that never applied.
            if (!field.Searchable.GetValueOrDefault())
            {
                return $"Field '{path}' names a synonym map but is not searchable; synonym maps " +
                       "apply only to full-text search.";
            }

            foreach (var name in field.SynonymMaps)
            {
                if (!existing.Contains(name))
                {
                    return $"Field '{path}' names synonym map '{name}', which does not exist.";
                }
            }
        }

        return null;
    }

    private static bool IsStringField(SearchField field)
        => field.Type == "Edm.String"
           || (field.IsCollection()
               && SearchFieldExtensions.GetCollectionElementType(field.Type) == "Edm.String");

    /// <summary>
    /// Walks the index's fields, including the sub-fields of complex types, pairing each with
    /// its full path, so that a mistake one level down is reported against the path that
    /// identifies it.
    /// </summary>
    private static IEnumerable<(SearchField Field, string Path)> EnumerateFields(
        IEnumerable<SearchField> fields,
        string prefix = "")
    {
        foreach (var field in fields)
        {
            var path = prefix + field.Name;

            yield return (field, path);

            foreach (var subField in EnumerateFields(field.Fields, path + ComplexTypeSupport.PathSeparator))
            {
                yield return subField;
            }
        }
    }
}
