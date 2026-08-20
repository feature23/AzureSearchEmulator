using AzureSearchEmulator.Models;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Miscellaneous;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Util;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Resolves the normalizer named by a field into the transformation it applies (issue #74).
/// </summary>
/// <remarks>
/// A normalizer is applied to a filter, facet or sort value, on both sides of the comparison:
/// to each value as it is indexed, and to the literal a query compares against it. That is what
/// makes <c>City eq 'las vegas'</c> match a document holding <c>"Las Vegas"</c> — not a
/// looser comparison, but the same fold applied to both, so the two become the same term.
///
/// <para><b>Why this is a string-to-string function rather than an analyzer.</b> Azure defines a
/// normalizer as a chain that always produces exactly one token, and the emulator has to hold
/// it to that: a filter compares one whole value, so a chain that emitted two tokens would
/// leave the comparison undefined. Running the chain here and taking its concatenated output
/// keeps that guarantee at the one place it can be enforced, and gives the callers — which
/// write Lucene terms and doc values, not token streams — the single string they need.</para>
///
/// <para>The chain itself is Lucene's, resolved through <see cref="CustomAnalyzerBuilder"/> so
/// that a char or token filter means exactly what it means inside a custom analyzer. Only the
/// tokenizer differs: Azure's normalizers do not name one, so a keyword tokenizer stands in,
/// which emits the whole input as a single token and leaves the filters to do the work.</para>
/// </remarks>
public static class NormalizerBuilder
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>
    /// The token filters Azure documents as usable in a normalizer.
    /// </summary>
    /// <remarks>
    /// A deliberate subset of the filters a custom analyzer may use. The excluded ones either
    /// split a token, drop it, or add more — <c>ngram</c>, <c>stopwords</c>, <c>synonym</c> and
    /// the stemmers among them — and any of those would break the one-token guarantee the
    /// comparison rests on. Azure rejects them at index creation, so
    /// <see cref="Indexing.NormalizerValidator"/> does too, rather than letting a definition
    /// through that would silently produce a value no filter could match.
    /// </remarks>
    public static readonly IReadOnlySet<string> SupportedTokenFilters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "arabic_normalization",
            "asciifolding",
            "cjk_width",
            "elision",
            "german_normalization",
            "hindi_normalization",
            "indic_normalization",
            "persian_normalization",
            "scandinavian_normalization",
            "scandinavian_folding",
            "sorani_normalization",
            "lowercase",
            "uppercase",
        };

    /// <summary>
    /// The char filters Azure documents as usable in a normalizer.
    /// </summary>
    /// <remarks>
    /// <c>html_strip</c> is the one a custom analyzer has that a normalizer does not; the other
    /// two are identical to their analyzer counterparts.
    /// </remarks>
    public static readonly IReadOnlySet<string> SupportedCharFilters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mapping",
            "pattern_replace",
        };

    /// <summary>
    /// The predefined normalizer names Azure publishes.
    /// </summary>
    /// <remarks>
    /// Each is a fixed chain of the same filters a custom normalizer composes, so they are
    /// expressed as one here rather than special-cased: <c>standard</c> is lowercase followed
    /// by asciifolding, and the rest are a single filter each.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string[]> PredefinedNormalizers =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["standard"] = ["lowercase", "asciifolding"],
            ["lowercase"] = ["lowercase"],
            ["uppercase"] = ["uppercase"],
            ["asciifolding"] = ["asciifolding"],
            ["elision"] = ["elision"],
        };

    /// <summary>
    /// Whether <paramref name="name"/> is one of Azure's predefined normalizers.
    /// </summary>
    /// <remarks>
    /// Separate from building one so that <see cref="Indexing.NormalizerValidator"/> can ask
    /// whether a name is known, and detect a custom definition that collides with it, without
    /// constructing the chain.
    /// </remarks>
    public static bool IsPredefined(string name) => PredefinedNormalizers.ContainsKey(name);

    /// <summary>
    /// Builds the normalizer <paramref name="name"/> refers to, resolving the index's own
    /// definitions before the predefined names.
    /// </summary>
    /// <exception cref="AnalyzerDefinitionException">
    /// The name is neither predefined nor defined by the index, or its definition names a
    /// component that cannot be built or is not allowed in a normalizer.
    /// </exception>
    public static Analyzer Build(SearchIndex index, string name)
    {
        if (index.FindNormalizer(name) is { } definition)
        {
            return Build(index, definition);
        }

        if (PredefinedNormalizers.TryGetValue(name, out var tokenFilters))
        {
            // Built through the same path as a custom definition so that a predefined name and
            // the custom definition spelling it out produce the same chain.
            return Build(index, new NormalizerDefinition
            {
                Name = name,
                TokenFilters = tokenFilters,
            });
        }

        throw new AnalyzerDefinitionException(
            $"'{name}' is not a supported normalizer name and is not defined by the index.");
    }

    /// <summary>
    /// Builds one normalizer definition into the Lucene chain that implements it.
    /// </summary>
    public static Analyzer Build(SearchIndex index, NormalizerDefinition definition)
    {
        var owner = $"Normalizer '{definition.Name}'";

        var tokenFilterFactories = definition.TokenFilters
            .Select(i => CustomAnalyzerBuilder.CreateTokenFilterFactory(index, owner, i))
            .ToList();

        var charFilterFactories = definition.CharFilters
            .Select(i => CustomAnalyzerBuilder.CreateCharFilterFactory(index, owner, i))
            .ToList();

        return Analyzer.NewAnonymous(
            createComponents: (_, reader) =>
            {
                // The whole input as one token, which is what a normalizer is defined to
                // produce; the filters then transform that token in the order declared.
                var tokenizer = new KeywordTokenizer(reader);

                TokenStream stream = tokenizer;

                foreach (var factory in tokenFilterFactories)
                {
                    stream = factory.Create(stream);
                }

                return new TokenStreamComponents(tokenizer, stream);
            },
            initReader: (_, reader) =>
            {
                foreach (var factory in charFilterFactories)
                {
                    reader = factory.Create(reader);
                }

                return reader;
            });
    }
}
