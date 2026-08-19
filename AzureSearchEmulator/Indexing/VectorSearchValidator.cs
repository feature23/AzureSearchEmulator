using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the vector search configuration of an index definition against the fields that use
/// it (issue #46).
/// </summary>
/// <remarks>
/// Azure validates vector configuration when the index is created, and the checks here follow
/// the same reasoning as <see cref="ScoringProfileValidator"/>: a field bound to a profile that
/// does not exist is a definition error, and reporting it at definition time points at the
/// mistake rather than leaving it to surface later as a query that cannot be answered.
///
/// It matters more here than for scoring profiles, because the failure is less visible. A
/// mistyped scoring profile name produces unboosted results; a mistyped vector profile name
/// produces a field with no metric, and so no defined ordering for any query against it.
/// </remarks>
public static class VectorSearchValidator
{
    /// <summary>
    /// Returns a message describing the first problem with the index's vector configuration, or
    /// null when it is usable.
    /// </summary>
    public static string? FindInvalidVectorSearch(SearchIndex index)
    {
        if (FindInvalidConfiguration(index.VectorSearch) is { } configurationError)
        {
            return configurationError;
        }

        foreach (var field in EnumerateFields(index.Fields))
        {
            if (FindInvalidField(index, field.Field, field.Path, field.InCollection) is { } fieldError)
            {
                return fieldError;
            }
        }

        return null;
    }

    private static string? FindInvalidConfiguration(VectorSearch? vectorSearch)
    {
        if (vectorSearch == null)
        {
            return null;
        }

        var algorithmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var algorithm in vectorSearch.Algorithms)
        {
            if (string.IsNullOrWhiteSpace(algorithm.Name))
            {
                return "A vector search algorithm must have a name.";
            }

            // Names are matched case-insensitively, so two differing only in case would make a
            // profile naming either one ambiguous.
            if (!algorithmNames.Add(algorithm.Name))
            {
                return $"The index has more than one vector search algorithm named '{algorithm.Name}'.";
            }
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in vectorSearch.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                return "A vector search profile must have a name.";
            }

            if (!profileNames.Add(profile.Name))
            {
                return $"The index has more than one vector search profile named '{profile.Name}'.";
            }

            if (string.IsNullOrWhiteSpace(profile.Algorithm))
            {
                return $"Vector search profile '{profile.Name}' must name an algorithm.";
            }

            if (vectorSearch.FindAlgorithm(profile.Algorithm) == null)
            {
                return $"Vector search profile '{profile.Name}' names algorithm " +
                       $"'{profile.Algorithm}', which is not defined on the index.";
            }

            // A vectorizer turns query text into an embedding by calling a hosted model, which
            // the emulator has no way to do. Accepting the definition would mean accepting an
            // index whose queries it must later refuse, so the refusal belongs here, where it
            // names the profile responsible.
            if (!string.IsNullOrWhiteSpace(profile.Vectorizer))
            {
                return $"Vector search profile '{profile.Name}' declares a vectorizer, which " +
                       "is not supported: generating embeddings requires a hosted embedding " +
                       "model. Supply precomputed embeddings instead.";
            }
        }

        return null;
    }

    private static string? FindInvalidField(
        SearchIndex index,
        SearchField field,
        string path,
        bool inComplexCollection)
    {
        var isVectorField = field.IsVectorField();

        // dimensions and vectorSearchProfile describe a vector, so they mean nothing anywhere
        // else. Accepting them silently on an ordinary field would hide a mistyped type.
        if (!isVectorField)
        {
            if (field.Dimensions != null)
            {
                return $"Field '{path}' is type {field.Type} and cannot declare dimensions; " +
                       $"only {VectorSearchSupport.VectorFieldType} fields can.";
            }

            if (field.VectorSearchProfile != null)
            {
                return $"Field '{path}' is type {field.Type} and cannot declare a " +
                       $"vectorSearchProfile; only {VectorSearchSupport.VectorFieldType} " +
                       "fields can.";
            }

            return null;
        }

        if (field.Dimensions is not { } dimensions)
        {
            return $"Vector field '{path}' must declare dimensions.";
        }

        if (dimensions < VectorSearchSupport.MinDimensions
            || dimensions > VectorSearchSupport.MaxDimensions)
        {
            return $"Vector field '{path}' declares {dimensions} dimensions, which must be " +
                   $"between {VectorSearchSupport.MinDimensions} and " +
                   $"{VectorSearchSupport.MaxDimensions}.";
        }

        if (string.IsNullOrWhiteSpace(field.VectorSearchProfile))
        {
            return $"Vector field '{path}' must name a vectorSearchProfile.";
        }

        // The profile is where the metric comes from, so a field naming one that does not exist
        // has no defined ordering for a query against it.
        if (index.VectorSearch?.FindProfile(field.VectorSearchProfile) == null)
        {
            return $"Vector field '{path}' names vectorSearchProfile " +
                   $"'{field.VectorSearchProfile}', which is not defined on the index.";
        }

        // A vector is not a term, so none of the term-based capabilities can apply to it. Azure
        // documents that filterable, sortable and facetable must all be false on a vector field,
        // and refuses the definition otherwise. The emulator refuses two of the three, for the
        // same reason: silently dropping a sortable flag would leave an $orderby that never
        // takes effect.
        //
        // filterable is the exception, and not for want of trying. This model declares it as a
        // non-nullable bool defaulting to true — a pre-existing divergence from Azure, where an
        // absent flag means false — so an omitted filterable and an explicit "filterable": true
        // are indistinguishable by the time validation sees them. The Azure SDK omits the flag
        // entirely when it writes a vector field, so refusing true here would refuse every
        // definition the SDK produces. Telling the two apart means making the property nullable,
        // which changes how every other field type is read and belongs in its own change rather
        // than smuggled in with vector search.
        //
        // Accepting it costs nothing in behaviour: the vector is never written as an indexed
        // term, so no filter can match it either way.
        if (field.Sortable.GetValueOrDefault())
        {
            return $"Vector field '{path}' cannot be sortable.";
        }

        if (field.Facetable.GetValueOrDefault())
        {
            return $"Vector field '{path}' cannot be facetable.";
        }

        if (field.Key.GetValueOrDefault())
        {
            return $"Vector field '{path}' cannot be the key field.";
        }

        // A document has one vector per field, and Lucene allows one doc-values entry per field
        // per document — so the several vectors the elements of a complex collection would
        // contribute have nowhere to go. Azure refuses the same combination. Refusing at
        // definition time reports it once, against the field responsible; allowing it would let
        // the index be created and then fail every upload that populated more than one element,
        // with an error naming a Lucene sidecar rather than the schema.
        if (inComplexCollection)
        {
            return $"Vector field '{path}' cannot be declared inside a " +
                   "Collection(Edm.ComplexType); a document can hold only one vector per field.";
        }

        return null;
    }

    /// <summary>
    /// Walks the index's fields, including the sub-fields of complex types, pairing each with
    /// its full path and whether it sits beneath a complex collection.
    /// </summary>
    /// <remarks>
    /// A vector field is legal inside a single complex type but not inside a collection of them
    /// — see <see cref="FindInvalidField"/> — and a mistake one level down deserves the same
    /// report as one at the top, naming the path that identifies it.
    /// </remarks>
    private static IEnumerable<(SearchField Field, string Path, bool InCollection)> EnumerateFields(
        IEnumerable<SearchField> fields,
        string prefix = "",
        bool inCollection = false)
    {
        foreach (var field in fields)
        {
            var path = prefix + field.Name;

            yield return (field, path, inCollection);

            // Once inside a complex collection every field below is too, however deeply nested.
            var childrenInCollection = inCollection || field.IsComplexCollection();

            foreach (var subField in EnumerateFields(
                         field.Fields,
                         path + ComplexTypeSupport.PathSeparator,
                         childrenInCollection))
            {
                yield return subField;
            }
        }
    }
}
