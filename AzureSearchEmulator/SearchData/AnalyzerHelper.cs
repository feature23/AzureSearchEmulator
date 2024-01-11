using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Ar;
using Lucene.Net.Analysis.Bg;
using Lucene.Net.Analysis.Br;
using Lucene.Net.Analysis.Ca;
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
using Lucene.Net.Analysis.Nl;
using Lucene.Net.Analysis.No;
using Lucene.Net.Analysis.Pt;
using Lucene.Net.Analysis.Ro;
using Lucene.Net.Analysis.Ru;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Sv;
using Lucene.Net.Analysis.Tr;
using Lucene.Net.Util;
using SearchField = AzureSearchEmulator.Models.SearchField;

namespace AzureSearchEmulator.SearchData;

public static class AnalyzerHelper
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    public static Analyzer GetAnalyzer(string? name)
    {
        return name switch
        {
            null => new StandardAnalyzer(Version),
            "standard" or "standard.lucene" => new StandardAnalyzer(Version),
            "keyword" => new KeywordAnalyzer(),
            "simple" => new SimpleAnalyzer(Version),
            "whitespace" => new WhitespaceAnalyzer(Version),
            "ar.lucene" => new ArabicAnalyzer(Version),
            "bg.lucene" => new BulgarianAnalyzer(Version),
            "ca.lucene" => new CatalanAnalyzer(Version),
            "cs.lucene" => new CzechAnalyzer(Version),
            "da.lucene" => new DanishAnalyzer(Version),
            "de.lucene" => new GermanAnalyzer(Version),
            "el.lucene" => new GreekAnalyzer(Version),
            "en.lucene" => new EnglishAnalyzer(Version),
            "es.lucene" => new SpanishAnalyzer(Version),
            "eu.lucene" => new BasqueAnalyzer(Version),
            "fa.lucene" => new PersianAnalyzer(Version),
            "fi.lucene" => new FinnishAnalyzer(Version),
            "fr.lucene" => new FrenchAnalyzer(Version),
            "ga.lucene" => new IrishAnalyzer(Version),
            "gl.lucene" => new GalicianAnalyzer(Version),
            "hi.lucene" => new HindiAnalyzer(Version),
            "hu.lucene" => new HungarianAnalyzer(Version),
            "hy.lucene" => new ArmenianAnalyzer(Version),
            "id.lucene" => new IndonesianAnalyzer(Version),
            "it.lucene" => new ItalianAnalyzer(Version),
            "lv.lucene" => new LatvianAnalyzer(Version),
            "nl.lucene" => new DutchAnalyzer(Version),
            "no.lucene" => new NorwegianAnalyzer(Version),
            "pt-Br.lucene" => new BrazilianAnalyzer(Version),
            "pt-Pt.lucene" => new PortugueseAnalyzer(Version),
            "ro.lucene" => new RomanianAnalyzer(Version),
            "ru.lucene" => new RussianAnalyzer(Version),
            "sv.lucene" => new SwedishAnalyzer(Version),
            "tr.lucene" => new TurkishAnalyzer(Version),
            _ => throw new NotSupportedException(), // TODO: Japanese, Korean, Polish, Thai, Chinese, "Microsoft", and custom analyzers
        };
    }

    public static Analyzer GetPerFieldSearchAnalyzer(IList<SearchField> fields)
    {
        var analyzers = fields
            .Select(i => (i.Name, Analyzer: i.SearchAnalyzer ?? i.Analyzer))
            .Where(i => i.Analyzer != null)
            .ToDictionary(i => i.Name, i => GetAnalyzer(i.Analyzer));

        return new PerFieldAnalyzerWrapper(new StandardAnalyzer(Version), analyzers);
    }

    public static Analyzer GetPerFieldIndexAnalyzer(IList<SearchField> fields)
    {
        var analyzers = fields
            .Select(i => (i.Name, Analyzer: i.IndexAnalyzer ?? i.Analyzer))
            .Where(i => i.Analyzer != null)
            .ToDictionary(i => i.Name, i => GetAnalyzer(i.Analyzer));

        return new PerFieldAnalyzerWrapper(new StandardAnalyzer(Version), analyzers);
    }
}
