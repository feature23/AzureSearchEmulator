using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AzureSearchEmulator.Models;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Synonym;
using LuceneSynonymMap = Lucene.Net.Analysis.Synonym.SynonymMap;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Applies synonym maps to the analyzer a query's terms are read with (issue #69).
/// </summary>
/// <remarks>
/// Query time only, which is what makes the feature safe to edit: see
/// <see cref="SynonymMap"/> for why expanding at index time would strand the documents
/// indexed before a rule change.
/// </remarks>
public static class SynonymMapHelper
{
    /// <summary>
    /// Compiled maps, keyed by the definition instance they were built from.
    /// </summary>
    /// <remarks>
    /// Parsing rules into an FST is the expensive part of this feature and it would otherwise
    /// happen on every search. Keying on the instance rather than the name means an edited map
    /// — a fresh object from the file repository — misses the cache and is rebuilt, so a
    /// search never answers from rules the caller has already replaced.
    /// </remarks>
    private static readonly ConditionalWeakTable<SynonymMap, LuceneSynonymMap> Cache = new();

    /// <summary>
    /// Wraps <paramref name="analyzer"/> so the tokens it produces are expanded by
    /// <paramref name="synonymMaps"/>, in the order the field names them.
    /// </summary>
    /// <remarks>
    /// Chained rather than merged: Azure lets a field name several maps, and running each as
    /// its own filter keeps a rule in one map from being combined with a rule in another into
    /// an expansion neither map describes.
    /// </remarks>
    public static Analyzer Wrap(Analyzer analyzer, IReadOnlyList<SynonymMap> synonymMaps)
    {
        if (synonymMaps.Count == 0)
        {
            return analyzer;
        }

        var compiled = synonymMaps.Select(Compile).ToList();

        return new SynonymAnalyzerWrapper(analyzer, compiled);
    }

    /// <summary>
    /// Compiles a map, reusing the previous result when the definition has not been replaced.
    /// </summary>
    public static LuceneSynonymMap Compile(SynonymMap synonymMap) =>
        Cache.GetValue(synonymMap, SynonymMapBuilder.Build);

    /// <summary>
    /// Resolves the maps a field names, skipping any the service no longer holds.
    /// </summary>
    /// <remarks>
    /// A missing map is skipped rather than thrown on. Azure refuses to delete a synonym map
    /// while an index still names it, but the emulator's indexes and maps are separate files
    /// that a user may edit or restore independently, and failing every search against the
    /// index would be a harsh answer to a dangling name that only widens results.
    /// </remarks>
    public static IReadOnlyList<SynonymMap> Resolve(
        SearchField field,
        IReadOnlyDictionary<string, SynonymMap> available)
    {
        if (field.SynonymMaps.Count == 0)
        {
            return [];
        }

        return field.SynonymMaps
            .Select(i => available.GetValueOrDefault(i))
            .OfType<SynonymMap>()
            .ToList();
    }

    /// <summary>
    /// An analyzer that appends a <see cref="SynonymFilter"/> per map to another analyzer's chain.
    /// </summary>
    /// <remarks>
    /// <see cref="AnalyzerWrapper"/> exists for exactly this: it delegates the tokenizer and the
    /// rest of the chain to the wrapped analyzer and lets the wrapper add to the token stream,
    /// so a field's own analyzer — custom, predefined or the default — keeps deciding how the
    /// text is split before any synonym is considered.
    /// </remarks>
    private sealed class SynonymAnalyzerWrapper(Analyzer inner, IReadOnlyList<LuceneSynonymMap> maps)
        : AnalyzerWrapper(inner.Strategy)
    {
        protected override Analyzer GetWrappedAnalyzer(string fieldName) => inner;

        protected override TokenStreamComponents WrapComponents(
            string fieldName,
            TokenStreamComponents components)
        {
            var stream = components.TokenStream;

            foreach (var map in maps)
            {
                // ignoreCase is false because the rules were already lower-cased when parsed,
                // and a field whose analyzer does not lower-case its tokens should not have
                // synonym matching quietly do it for them.
                stream = new SynonymFilter(stream, map, ignoreCase: false);
            }

            return new TokenStreamComponents(components.Tokenizer, stream);
        }
    }
}
