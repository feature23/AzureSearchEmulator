using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;

namespace AzureSearchEmulator.Indexing;

/// <summary>
/// Checks the analyzers an index defines and the names its fields refer to (issue #34).
/// </summary>
/// <remarks>
/// Azure validates analyzer names when the index is created, and refuses a field naming an
/// analyzer that is neither predefined nor defined on the index. The emulator used to accept
/// any name at creation and throw a bare <see cref="NotSupportedException"/> later, the first
/// time a document was indexed into the field — which meant the error arrived far from the
/// mistake, named neither the field nor the analyzer, and surfaced as an unstructured 500.
///
/// Validating here follows the reasoning of <see cref="VectorSearchValidator"/>: a bad analyzer
/// name is a definition error, and reporting it at definition time points at the mistake.
///
/// It also checks that each definition can actually be built. A custom analyzer naming a
/// tokenizer that does not exist is well-formed JSON and passes a name check, but produces
/// nothing usable; building it once here means the failure is reported against the definition
/// rather than against the first document that happens to reach the field.
/// </remarks>
public static class AnalyzerValidator
{
    /// <summary>
    /// Returns a message describing the first problem with the index's analyzers, or null when
    /// they are usable.
    /// </summary>
    public static string? FindInvalidAnalyzer(SearchIndex index)
    {
        if (FindInvalidDefinitions(index) is { } definitionError)
        {
            return definitionError;
        }

        return FindInvalidFieldReference(index);
    }

    private static string? FindInvalidDefinitions(SearchIndex index)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var analyzer in index.Analyzers)
        {
            if (string.IsNullOrWhiteSpace(analyzer.Name))
            {
                return "An analyzer must have a name.";
            }

            // Names are matched case-insensitively, so two differing only in case would make a
            // field naming either one ambiguous.
            if (!names.Add(analyzer.Name))
            {
                return $"The index has more than one analyzer named '{analyzer.Name}'.";
            }

            // Azure refuses a custom analyzer that takes the name of a built-in. Allowing it
            // would make the definition unreachable — the name would keep resolving to whatever
            // the lookup order favoured — so the collision is reported rather than silently
            // resolved one way.
            if (AnalyzerHelper.TryCreatePredefined(analyzer.Name) != null)
            {
                return $"Analyzer '{analyzer.Name}' takes the name of a predefined analyzer; " +
                       "a custom analyzer must have a name of its own.";
            }

            // Built once here so that a definition which cannot produce a working analyzer is
            // refused with the builder's own message, which names the component at fault.
            try
            {
                CustomAnalyzerBuilder.Build(index, analyzer);
            }
            catch (AnalyzerDefinitionException ex)
            {
                return ex.Message;
            }
        }

        if (FindInvalidComponentNames(index.Tokenizers, "tokenizer") is { } tokenizerError)
        {
            return tokenizerError;
        }

        if (FindInvalidComponentNames(index.TokenFilters, "token filter") is { } tokenFilterError)
        {
            return tokenFilterError;
        }

        return FindInvalidComponentNames(index.CharFilters, "char filter");
    }

    private static string? FindInvalidComponentNames(
        IList<AnalysisComponentDefinition> components,
        string kind)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.Name))
            {
                return $"A {kind} must have a name.";
            }

            if (!names.Add(component.Name))
            {
                return $"The index has more than one {kind} named '{component.Name}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Checks every analyzer name a field refers to, including on the sub-fields of a complex
    /// type.
    /// </summary>
    private static string? FindInvalidFieldReference(SearchIndex index)
    {
        foreach (var (field, path) in EnumerateFields(index.Fields))
        {
            foreach (var (name, property) in new[]
                     {
                         (field.Analyzer, "analyzer"),
                         (field.IndexAnalyzer, "indexAnalyzer"),
                         (field.SearchAnalyzer, "searchAnalyzer"),
                     })
            {
                if (name == null)
                {
                    continue;
                }

                if (index.FindAnalyzer(name) == null && AnalyzerHelper.TryCreatePredefined(name) == null)
                {
                    return $"Field '{path}' names {property} '{name}', which is not a valid " +
                           "lexical analyzer name and is not defined on the index.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Walks the index's fields, including the sub-fields of complex types, pairing each with
    /// its full path.
    /// </summary>
    /// <remarks>
    /// A mistyped analyzer name one level down deserves the same report as one at the top,
    /// naming the path that identifies it.
    /// </remarks>
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
