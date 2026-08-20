using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the normalizers an index defines and the names its fields refer to (issue #74).
/// </summary>
/// <remarks>
/// Follows <see cref="AnalyzerValidator"/> in both structure and reasoning: a bad normalizer
/// name is a definition error, and reporting it when the index is created points at the
/// mistake, rather than surfacing later as an unstructured failure against whichever document
/// first reached the field.
///
/// It checks more than the analyzer validator does, because Azure constrains where a normalizer
/// may be used as well as what it may contain. A normalizer applies to filter, facet and sort
/// values, so it is meaningful only on a string field that at least one of those can reach; and
/// its chain must preserve the single token those comparisons rely on, so only the filters
/// Azure documents for normalizers are allowed. Both are refused here rather than accepted and
/// quietly ignored, since an index that names a normalizer it will never apply is a mistake
/// worth reporting.
/// </remarks>
public static class NormalizerValidator
{
    /// <summary>
    /// Returns a message describing the first problem with the index's normalizers, or null
    /// when they are usable.
    /// </summary>
    public static string? FindInvalidNormalizer(SearchIndex index)
        => FindInvalidDefinitions(index) ?? FindInvalidFieldReference(index);

    private static string? FindInvalidDefinitions(SearchIndex index)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var normalizer in index.Normalizers)
        {
            if (string.IsNullOrWhiteSpace(normalizer.Name))
            {
                return "A normalizer must have a name.";
            }

            // Names are matched case-insensitively, so two differing only in case would make a
            // field naming either one ambiguous.
            if (!names.Add(normalizer.Name))
            {
                return $"The index has more than one normalizer named '{normalizer.Name}'.";
            }

            // Azure refuses a custom normalizer that takes the name of a predefined one, for
            // the reason AnalyzerValidator gives: the definition would be unreachable behind
            // whichever lookup answered first.
            if (NormalizerBuilder.IsPredefined(normalizer.Name))
            {
                return $"Normalizer '{normalizer.Name}' takes the name of a predefined " +
                       "normalizer; a custom normalizer must have a name of its own.";
            }

            if (FindUnsupportedComponent(index, normalizer) is { } componentError)
            {
                return componentError;
            }

            // Built once here so that a definition which cannot produce a working normalizer is
            // refused with the builder's own message, which names the component at fault.
            try
            {
                NormalizerBuilder.Build(index, normalizer);
            }
            catch (AnalyzerDefinitionException ex)
            {
                return ex.Message;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks the chain against the filters Azure allows in a normalizer.
    /// </summary>
    /// <remarks>
    /// A name refers either to a component the index defines — whose <c>@odata.type</c> is what
    /// identifies it — or straight to a built-in, where the name is the identifier. Both are
    /// resolved to the built-in's name before the check, so that defining a tokenizer-splitting
    /// filter under an innocuous name does not slip past it.
    /// </remarks>
    private static string? FindUnsupportedComponent(SearchIndex index, NormalizerDefinition normalizer)
    {
        foreach (var (name, defined, supported, kind) in new[]
                 {
                     (normalizer.TokenFilters, index.TokenFilters,
                         NormalizerBuilder.SupportedTokenFilters, "token filter"),
                     (normalizer.CharFilters, index.CharFilters,
                         NormalizerBuilder.SupportedCharFilters, "char filter"),
                 })
        {
            foreach (var component in name)
            {
                var type = ResolveComponentType(defined, component);

                if (!supported.Contains(type))
                {
                    return $"Normalizer '{normalizer.Name}' names {kind} '{component}', which " +
                           $"is not one of the {kind}s a normalizer may use. A normalizer must " +
                           "produce a single token, so only the filters that preserve one are " +
                           "allowed.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The built-in a component name resolves to: its definition's type where the index defines
    /// one, and otherwise the name itself.
    /// </summary>
    private static string ResolveComponentType(
        IEnumerable<AnalysisComponentDefinition> defined,
        string name)
    {
        var definition = defined.FirstOrDefault(
            i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        return definition?.ODataType is { } odataType
            ? CustomAnalyzerBuilder.StripODataPrefix(odataType)
            : name;
    }

    /// <summary>
    /// Checks every normalizer name a field refers to, including on the sub-fields of a complex
    /// type, and that the field is one a normalizer can apply to.
    /// </summary>
    private static string? FindInvalidFieldReference(SearchIndex index)
    {
        foreach (var (field, path) in EnumerateFields(index.Fields))
        {
            if (field.Normalizer is not { } name)
            {
                continue;
            }

            if (index.FindNormalizer(name) == null && !NormalizerBuilder.IsPredefined(name))
            {
                return $"Field '{path}' names normalizer '{name}', which is not a valid " +
                       "normalizer name and is not defined on the index.";
            }

            if (!IsStringField(field))
            {
                return $"Field '{path}' is type {field.Type} and cannot have a normalizer; " +
                       "normalizers apply to Edm.String and Collection(Edm.String) fields.";
            }

            // A normalizer only ever runs on a value a filter, facet or sort reads, so a field
            // none of them can reach would carry one that never applied.
            if (!field.Filterable && !field.IsSortable() && !field.Facetable.GetValueOrDefault())
            {
                return $"Field '{path}' names a normalizer but is not filterable, sortable or " +
                       "facetable; a normalizer applies only to those operations.";
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
