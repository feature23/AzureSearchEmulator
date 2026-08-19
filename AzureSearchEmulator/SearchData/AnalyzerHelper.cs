using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Ar;
using Lucene.Net.Analysis.Bg;
using Lucene.Net.Analysis.Br;
using Lucene.Net.Analysis.Ca;
using Lucene.Net.Analysis.Cjk;
using Lucene.Net.Analysis.Ckb;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Cz;
using Lucene.Net.Analysis.Da;
using Lucene.Net.Analysis.De;
using Lucene.Net.Analysis.El;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Es;
using Lucene.Net.Analysis.Eu;
using Lucene.Net.Analysis.Fa;
using Lucene.Net.Analysis.Fi;
using Lucene.Net.Analysis.Fr;
using Lucene.Net.Analysis.Ga;
using Lucene.Net.Analysis.Gl;
using Lucene.Net.Analysis.Hi;
using Lucene.Net.Analysis.Hu;
using Lucene.Net.Analysis.Hy;
using Lucene.Net.Analysis.Id;
using Lucene.Net.Analysis.It;
using Lucene.Net.Analysis.Lv;
using Lucene.Net.Analysis.Miscellaneous;
using System.Text.RegularExpressions;
using Lucene.Net.Analysis.Nl;
using Lucene.Net.Analysis.No;
using Lucene.Net.Analysis.Pt;
using Lucene.Net.Analysis.Ro;
using Lucene.Net.Analysis.Ru;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Sv;
using Lucene.Net.Analysis.Tr;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Util;
using SearchField = AzureSearchEmulator.Models.SearchField;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Resolves the analyzer named by a field into the Lucene analyzer that implements it.
/// </summary>
/// <remarks>
/// A name is resolved against the index's own <c>analyzers</c> first and the predefined set
/// second, which is the order Azure documents: a custom analyzer may not take the name of a
/// built-in, so the two sets never collide and the order only decides which lookup answers.
///
/// <para><b>On the predefined names.</b> Azure publishes 85 in its <c>LexicalAnalyzerName</c>
/// enum, in two flavours. The <c>.lucene</c> names are backed by the same Apache Lucene
/// analyzers this emulator runs on, so those mappings are equivalences.</para>
///
/// <para>The <c>.microsoft</c> names are not. Azure backs those with the proprietary
/// natural-language stack from Office and Bing, which lemmatizes rather than stems, decompounds
/// in the Germanic and Finno-Ugric languages, and recognizes entities such as URLs and dates.
/// None of that exists in Lucene. Each is mapped to the closest Lucene analyzer for the same
/// language anyway, because refusing them is worse for the emulator's purpose: <c>.microsoft</c>
/// is Azure's default flavour for a language field, so rejecting the name would mean a
/// realistic index definition could not be created at all, while accepting it gives tokenization
/// good enough for development and test. Stemming will not agree token-for-token with the
/// service, and a test asserting on exact stems of a <c>.microsoft</c> field should not be
/// expected to.</para>
///
/// <para>Languages Lucene has no analyzer for — Hebrew, Thai, Vietnamese, the Indic languages
/// beyond Hindi, and several smaller European ones — fall back to the standard analyzer, which
/// segments on Unicode text boundaries. What is lost there is the stemming, not the
/// tokenization.</para>
///
/// <para>Names are matched case-insensitively. Azure's enum spells the region-qualified names
/// <c>pt-BR</c>, <c>zh-Hans</c> and <c>sr-cyrillic</c> with no consistent rule between them,
/// and its own prose documentation contradicts the enum on the Portuguese pair. Matching
/// case-insensitively means the emulator does not have to adjudicate that, and no client is
/// refused over the capitalization of a region code — which is what used to happen to
/// <c>pt-BR.lucene</c>, since only the <c>pt-Br</c> spelling was matched.</para>
/// </remarks>
public static class AnalyzerHelper
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>
    /// The analyzer applied where none is named.
    /// </summary>
    /// <remarks>
    /// Azure documents the default as the Standard Lucene analyzer, for both indexing and
    /// searching.
    /// </remarks>
    public static Analyzer CreateDefault() => new StandardAnalyzer(Version);

    /// <summary>
    /// Resolves a predefined analyzer name, or null when the name is not one Azure publishes.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="GetAnalyzer(SearchIndex?, string?)"/> so that
    /// <see cref="AnalyzerValidator"/> can ask whether a name is known without building the
    /// analyzer, and so a custom analyzer that collides with a predefined name can be detected.
    /// </remarks>
    public static Analyzer? TryCreatePredefined(string name)
    {
        return name.ToLowerInvariant() switch
        {
            // Language-neutral. "standard" is not in the formal enum, but Azure's analyzer
            // documentation uses the bare spelling in its examples and lists it as another name
            // for standard.lucene, so both are accepted.
            "standard" or "standard.lucene" => new StandardAnalyzer(Version),

            // Folds accented characters to their ASCII equivalents before standard analysis, so
            // that "resume" matches "résumé".
            "standardasciifolding.lucene" => Analyzer.NewAnonymous((_, reader) =>
            {
                var tokenizer = new StandardTokenizer(Version, reader);
                TokenStream stream = new StandardFilter(Version, tokenizer);
                stream = new LowerCaseFilter(Version, stream);
                stream = new ASCIIFoldingFilter(stream);
                return new TokenStreamComponents(tokenizer, stream);
            }),

            "keyword" => new KeywordAnalyzer(),
            // Azure's default pattern splits on runs of non-word characters, lowercases, and
            // removes no stopwords.
            "pattern" => CustomAnalyzerBuilder.CreatePatternAnalyzer(
                new Regex(@"\W+", RegexOptions.Compiled), lowerCase: true, stopwords: null),
            "simple" => new SimpleAnalyzer(Version),
            "stop" => new StopAnalyzer(Version),
            "whitespace" => new WhitespaceAnalyzer(Version),

            // Languages with a Lucene analyzer of their own. Both flavours resolve here; see
            // the remarks on the type for why .microsoft is answered with the Lucene one.
            "ar.lucene" or "ar.microsoft" => new ArabicAnalyzer(Version),
            "bg.lucene" or "bg.microsoft" => new BulgarianAnalyzer(Version),
            "ca.lucene" or "ca.microsoft" => new CatalanAnalyzer(Version),
            "cs.lucene" or "cs.microsoft" => new CzechAnalyzer(Version),
            "da.lucene" or "da.microsoft" => new DanishAnalyzer(Version),
            "de.lucene" or "de.microsoft" => new GermanAnalyzer(Version),
            "el.lucene" or "el.microsoft" => new GreekAnalyzer(Version),
            "en.lucene" or "en.microsoft" => new EnglishAnalyzer(Version),
            "es.lucene" or "es.microsoft" => new SpanishAnalyzer(Version),
            "eu.lucene" => new BasqueAnalyzer(Version),
            "fa.lucene" => new PersianAnalyzer(Version),
            "fi.lucene" or "fi.microsoft" => new FinnishAnalyzer(Version),
            "fr.lucene" or "fr.microsoft" => new FrenchAnalyzer(Version),
            "ga.lucene" => new IrishAnalyzer(Version),
            "gl.lucene" => new GalicianAnalyzer(Version),
            "hi.lucene" or "hi.microsoft" => new HindiAnalyzer(Version),
            "hu.lucene" or "hu.microsoft" => new HungarianAnalyzer(Version),
            "hy.lucene" => new ArmenianAnalyzer(Version),
            "id.lucene" or "id.microsoft" => new IndonesianAnalyzer(Version),
            "it.lucene" or "it.microsoft" => new ItalianAnalyzer(Version),
            "lv.lucene" or "lv.microsoft" => new LatvianAnalyzer(Version),
            "nl.lucene" or "nl.microsoft" => new DutchAnalyzer(Version),
            "no.lucene" or "nb.microsoft" => new NorwegianAnalyzer(Version),
            "pt-br.lucene" or "pt-br.microsoft" => new BrazilianAnalyzer(Version),
            "pt-pt.lucene" or "pt-pt.microsoft" => new PortugueseAnalyzer(Version),
            "ro.lucene" or "ro.microsoft" => new RomanianAnalyzer(Version),
            "ru.lucene" or "ru.microsoft" => new RussianAnalyzer(Version),
            "sv.lucene" or "sv.microsoft" => new SwedishAnalyzer(Version),
            "tr.lucene" or "tr.microsoft" => new TurkishAnalyzer(Version),

            // Not space-delimited, so whitespace tokenization would make a whole clause one
            // term. CJKAnalyzer forms overlapping bigrams of CJK characters, which is Lucene's
            // standard approach and makes the text searchable, though it is not the
            // morphological segmentation Azure performs.
            "zh-hans.lucene" or "zh-hans.microsoft"
                or "zh-hant.lucene" or "zh-hant.microsoft"
                or "ja.lucene" or "ja.microsoft"
                or "ko.lucene" or "ko.microsoft" => new CJKAnalyzer(Version),

            // Sorani Kurdish has a Lucene analyzer but no Azure name of its own; it is reached
            // only as the closest match for Kurdish text.
            "ckb.lucene" => new SoraniAnalyzer(Version),

            // Polish and Thai have Azure analyzers but no Lucene.NET implementation in the
            // packages this project references — Polish needs the Stempel contrib, and Thai
            // needs a segmentation library. Answered with the standard analyzer rather than
            // refused, so that an index naming them can still be created and searched on
            // whitespace and punctuation boundaries.
            "pl.lucene" or "pl.microsoft" or "th.lucene" or "th.microsoft" => new StandardAnalyzer(Version),

            // Every remaining .microsoft language: Azure has an analyzer, Lucene has none, and
            // the standard analyzer's Unicode segmentation is the honest fallback.
            "bn.microsoft" or "et.microsoft" or "gu.microsoft" or "he.microsoft"
                or "hr.microsoft" or "is.microsoft" or "kn.microsoft" or "lt.microsoft"
                or "ml.microsoft" or "mr.microsoft" or "ms.microsoft" or "pa.microsoft"
                or "sk.microsoft" or "sl.microsoft" or "sr-cyrillic.microsoft"
                or "sr-latin.microsoft" or "ta.microsoft" or "te.microsoft"
                or "uk.microsoft" or "ur.microsoft" or "vi.microsoft" => new StandardAnalyzer(Version),

            _ => null
        };
    }

    /// <summary>
    /// Resolves the analyzer named by a field, falling back to the default when it names none.
    /// </summary>
    /// <param name="index">
    /// The index whose custom analyzers the name is resolved against, or null where only the
    /// predefined names are in play.
    /// </param>
    /// <exception cref="AnalyzerDefinitionException">
    /// The name is neither predefined nor defined by the index, or its definition cannot be
    /// built. <see cref="AnalyzerValidator"/> refuses both at index creation, so reaching this
    /// means an index was written before that validation existed.
    /// </exception>
    public static Analyzer GetAnalyzer(SearchIndex? index, string? name)
    {
        if (name == null)
        {
            return CreateDefault();
        }

        if (index != null && CustomAnalyzerBuilder.TryBuild(index, name) is { } custom)
        {
            return custom;
        }

        return TryCreatePredefined(name)
               ?? throw new AnalyzerDefinitionException(
                   $"'{name}' is not a supported lexical analyzer name and is not defined by " +
                   "the index.");
    }

    public static Analyzer GetPerFieldSearchAnalyzer(SearchIndex index)
        => GetPerFieldAnalyzer(index, i => i.SearchAnalyzer ?? i.Analyzer);

    public static Analyzer GetPerFieldIndexAnalyzer(SearchIndex index)
        => GetPerFieldAnalyzer(index, i => i.IndexAnalyzer ?? i.Analyzer);

    /// <summary>
    /// Builds a per-field analyzer keyed by each leaf field's Lucene field name.
    /// </summary>
    /// <remarks>
    /// Sub-fields of a complex type are indexed under their full slash-delimited path, so
    /// they are registered under that path here too — keying them by their bare name would
    /// silently leave them on the default analyzer.
    /// </remarks>
    private static Analyzer GetPerFieldAnalyzer(SearchIndex index, Func<SearchField, string?> selectAnalyzer)
    {
        var analyzers = index.Fields
            .SelectMany(i => ComplexTypeSupport.EnumerateLeafFields(i))
            .Select(i => (i.Path, Analyzer: selectAnalyzer(i.Field)))
            .Where(i => i.Analyzer != null)
            .ToDictionary(i => i.Path, i => GetAnalyzer(index, i.Analyzer));

        return new PerFieldAnalyzerWrapper(CreateDefault(), analyzers);
    }
}
