using System.Diagnostics.CodeAnalysis;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Helpers for the <c>Edm.ComplexType</c> and <c>Collection(Edm.ComplexType)</c> field types.
/// </summary>
/// <remarks>
/// Azure Search addresses a complex type's sub-fields by a slash-delimited path, i.e.
/// <c>Address/City</c> in a <c>$filter</c>, <c>$orderby</c>, or <c>searchFields</c>. The
/// emulator mirrors that by flattening each leaf sub-field into a Lucene field whose name is
/// that same path, so the existing query machinery — which only ever deals in flat field
/// names — keeps working unchanged.
///
/// The flattening is lossy in one direction: it cannot reconstruct the original object shape,
/// and for a collection of complex objects it cannot say which leaf values belonged to the
/// same element. Retrieval therefore reads back from a stored JSON sidecar rather than from
/// the flattened leaves (see <see cref="GetComplexStorageFieldName"/>), which keeps a
/// document's round trip faithful.
/// </remarks>
public static class ComplexTypeSupport
{
    public const string ComplexType = "Edm.ComplexType";

    public const string ComplexCollectionType = "Collection(Edm.ComplexType)";

    /// <summary>
    /// Separator between a complex field and its sub-field, matching Azure Search's syntax.
    /// </summary>
    public const char PathSeparator = '/';

    public static bool IsComplex(this SearchField field)
        => field.Type is ComplexType or ComplexCollectionType;

    /// <summary>
    /// True for <c>Collection(Edm.ComplexType)</c>, whose values Azure Search only allows to
    /// be filtered through an <c>any</c>/<c>all</c> lambda.
    /// </summary>
    public static bool IsComplexCollection(this SearchField field)
        => field.Type == ComplexCollectionType;

    /// <summary>
    /// The Lucene field name for a leaf reached by <paramref name="path"/>, i.e.
    /// <c>Address/City</c>.
    /// </summary>
    public static string CombinePath(string? parentPath, string fieldName)
        => string.IsNullOrEmpty(parentPath) ? fieldName : parentPath + PathSeparator + fieldName;

    /// <summary>
    /// Sidecar field holding the original JSON of a complex value, so retrieval can
    /// reproduce the object exactly rather than rebuilding it from flattened leaves.
    /// </summary>
    public static string GetComplexStorageFieldName(string fieldName) => "__azs_complex__" + fieldName;

    /// <summary>
    /// Walks a complex field's sub-fields, yielding every leaf together with its full
    /// slash-delimited path.
    /// </summary>
    /// <remarks>
    /// Nested complex types recurse, so <c>Address/Geo/Lat</c> resolves as one leaf. The
    /// complex fields themselves are not yielded: they hold no value of their own.
    /// </remarks>
    public static IEnumerable<(string Path, SearchField Field)> EnumerateLeafFields(
        SearchField field,
        string? parentPath = null)
    {
        var path = CombinePath(parentPath, field.Name);

        if (!field.IsComplex())
        {
            yield return (path, field);
            yield break;
        }

        foreach (var subField in field.Fields)
        {
            foreach (var leaf in EnumerateLeafFields(subField, path))
            {
                yield return leaf;
            }
        }
    }

    /// <summary>
    /// Walks every field of an index, yielding each leaf with its full path. Non-complex
    /// top-level fields yield themselves under their own name.
    /// </summary>
    public static IEnumerable<(string Path, SearchField Field)> EnumerateLeafFields(SearchIndex index)
        => index.Fields.SelectMany(f => EnumerateLeafFields(f));

    /// <summary>
    /// Resolves a slash-delimited path such as <c>Address/City</c> to the field it names,
    /// or null when no such field exists.
    /// </summary>
    /// <remarks>
    /// Matching is case-insensitive at every segment, consistent with how the rest of the
    /// emulator resolves field names from query expressions.
    /// </remarks>
    public static SearchField? FindFieldByPath(SearchIndex index, string path)
        => TryResolvePath(index, path, out var field, out _) ? field : null;

    /// <summary>
    /// Resolves a slash-delimited path to its field and to the canonically-cased path the
    /// value is indexed under.
    /// </summary>
    /// <remarks>
    /// Query expressions may name a field in any casing, but Lucene field names are
    /// case-sensitive, so the schema's own spelling — not the caller's — has to be used when
    /// building a query or a sort.
    /// </remarks>
    public static bool TryResolvePath(
        SearchIndex index,
        string path,
        [NotNullWhen(true)] out SearchField? field,
        out string canonicalPath)
    {
        field = null;
        canonicalPath = path;

        var segments = path.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        SearchField? current = null;
        string resolved = string.Empty;

        foreach (var segment in segments)
        {
            var candidates = current?.Fields ?? index.Fields;

            var next = candidates.FirstOrDefault(f => f.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));

            if (next is null)
            {
                return false;
            }

            current = next;
            resolved = CombinePath(resolved, next.Name);
        }

        if (current is null)
        {
            return false;
        }

        field = current;
        canonicalPath = resolved;
        return true;
    }

    /// <summary>
    /// Returns the path of the nearest ancestor of <paramref name="path"/> that is a
    /// <c>Collection(Edm.ComplexType)</c>, or null when the path sits under no such
    /// collection.
    /// </summary>
    /// <remarks>
    /// Used to reject operations that need a single value per document — sorting, for
    /// instance — since a sub-field under a complex collection has one value per element.
    /// </remarks>
    public static string? FindComplexCollectionAncestorPath(SearchIndex index, string path)
    {
        var segments = path.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        SearchField? current = null;
        string? currentPath = null;

        // The last segment is the leaf itself; only its ancestors are of interest here.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var candidates = current?.Fields ?? index.Fields;

            current = candidates.FirstOrDefault(f => f.Name.Equals(segments[i], StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                return null;
            }

            currentPath = CombinePath(currentPath, current.Name);

            if (current.IsComplexCollection())
            {
                return currentPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Validates that a complex field declares sub-fields and that its sub-fields are
    /// themselves well-formed, mirroring the errors Azure Search returns at index creation.
    /// </summary>
    public static void ValidateComplexField(SearchField field, string? parentPath = null)
    {
        var path = CombinePath(parentPath, field.Name);

        if (!field.IsComplex())
        {
            return;
        }

        if (field.Fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Complex field '{path}' must declare at least one sub-field.");
        }

        if (field.Key.GetValueOrDefault())
        {
            throw new InvalidOperationException($"Complex field '{path}' cannot be the key field.");
        }

        foreach (var subField in field.Fields)
        {
            if (subField.Key.GetValueOrDefault())
            {
                throw new InvalidOperationException(
                    $"Sub-field '{CombinePath(path, subField.Name)}' cannot be the key field; "
                    + "only a top-level Edm.String field may be the key.");
            }

            ValidateComplexField(subField, path);
        }
    }
}
