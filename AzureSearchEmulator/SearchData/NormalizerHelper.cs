using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using AzureSearchEmulator.Models;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.TokenAttributes;
using SearchField = AzureSearchEmulator.Models.SearchField;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Applies a field's normalizer to a value (issue #74).
/// </summary>
/// <remarks>
/// The one place indexing and querying both go through, which is what makes the feature work:
/// a normalizer only matches anything if the identical transformation reaches the value written
/// into the index and the literal a filter compares against it. Splitting that across two
/// implementations would mean a difference between them showed up as a filter that silently
/// matches nothing.
///
/// Built chains are cached per index and name. Building one resolves every char and token
/// filter through Lucene's SPI, which is far too much work to repeat for each value of each
/// document; the cache keeps it to once per normalizer per index revision.
/// </remarks>
public static class NormalizerHelper
{
    /// <summary>
    /// Chains already built, keyed by the index they were built from and the name they were
    /// built for.
    /// </summary>
    /// <remarks>
    /// Keyed on the index instance rather than its name so that a definition change cannot be
    /// served a chain built from the previous one: a rewritten index is a different object,
    /// and the table holds only weak references to the old one, so the entry goes with it.
    /// </remarks>
    private static readonly ConditionalWeakTable<
        SearchIndex,
        ConcurrentDictionary<string, Analyzer>> Cache = new();

    /// <summary>
    /// Normalizes <paramref name="value"/> for <paramref name="field"/>, returning it unchanged
    /// when the field names no normalizer.
    /// </summary>
    /// <exception cref="AnalyzerDefinitionException">
    /// The field names a normalizer that cannot be resolved or built.
    /// <see cref="Indexing.NormalizerValidator"/> refuses both at index creation, so reaching
    /// this means an index was written before that validation existed.
    /// </exception>
    public static string Normalize(SearchIndex? index, SearchField field, string value)
        => index == null || field.Normalizer == null
            ? value
            : Normalize(index, field.Normalizer, value);

    /// <summary>
    /// Normalizes <paramref name="value"/> through the normalizer named
    /// <paramref name="name"/>.
    /// </summary>
    public static string Normalize(SearchIndex index, string name, string value)
    {
        var normalizer = Cache.GetOrCreateValue(index)
            .GetOrAdd(name, n => NormalizerBuilder.Build(index, n));

        return Apply(normalizer, value);
    }

    /// <summary>
    /// Runs the chain over the value and returns what it produced.
    /// </summary>
    /// <remarks>
    /// A normalizer is defined to produce exactly one token, and
    /// <see cref="Indexing.NormalizerValidator"/> only admits filters that preserve that. The
    /// tokens are nevertheless concatenated rather than the first one taken: should a chain
    /// still manage to split, keeping every piece leaves the value distinguishable from a
    /// shorter one that shares its first token, where taking the first would silently conflate
    /// them.
    ///
    /// An empty result is returned as such. A filter that removes the token — which the allowed
    /// set should not, but a mapping char filter can empty the text — leaves an empty value,
    /// and that is a real value to compare on, not an absence.
    /// </remarks>
    private static string Apply(Analyzer normalizer, string value)
    {
        using var stream = normalizer.GetTokenStream(fieldName: "", value);
        var term = stream.AddAttribute<ICharTermAttribute>();

        stream.Reset();

        string? single = null;
        StringBuilder? builder = null;

        while (stream.IncrementToken())
        {
            if (single == null)
            {
                single = term.ToString();
                continue;
            }

            builder ??= new StringBuilder(single);
            builder.Append(term.ToString());
        }

        stream.End();

        return builder?.ToString() ?? single ?? "";
    }
}
