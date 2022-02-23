using Azure.Search.Documents.Indexes.Models;
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
            LexicalAnalyzerName.Values.StandardLucene => new StandardAnalyzer(Version),
            LexicalAnalyzerName.Values.Keyword => new KeywordAnalyzer(),
            LexicalAnalyzerName.Values.Simple => new SimpleAnalyzer(Version),
            LexicalAnalyzerName.Values.Whitespace => new WhitespaceAnalyzer(Version),
            LexicalAnalyzerName.Values.ArLucene => new ArabicAnalyzer(Version),
            LexicalAnalyzerName.Values.BgLucene => new BulgarianAnalyzer(Version),
            LexicalAnalyzerName.Values.CaLucene => new CatalanAnalyzer(Version),
            LexicalAnalyzerName.Values.CsLucene => new CzechAnalyzer(Version),
            LexicalAnalyzerName.Values.DaLucene => new DanishAnalyzer(Version),
            LexicalAnalyzerName.Values.DeLucene => new GermanAnalyzer(Version),
            LexicalAnalyzerName.Values.ElLucene => new GreekAnalyzer(Version),
            LexicalAnalyzerName.Values.EnLucene => new EnglishAnalyzer(Version),
            LexicalAnalyzerName.Values.EsLucene => new SpanishAnalyzer(Version),
            LexicalAnalyzerName.Values.EuLucene => new BasqueAnalyzer(Version),
            LexicalAnalyzerName.Values.FaLucene => new PersianAnalyzer(Version),
            LexicalAnalyzerName.Values.FiLucene => new FinnishAnalyzer(Version),
            LexicalAnalyzerName.Values.FrLucene => new FrenchAnalyzer(Version),
            LexicalAnalyzerName.Values.GaLucene => new IrishAnalyzer(Version),
            LexicalAnalyzerName.Values.GlLucene => new GalicianAnalyzer(Version),
            LexicalAnalyzerName.Values.HiLucene => new HindiAnalyzer(Version),
            LexicalAnalyzerName.Values.HuLucene => new HungarianAnalyzer(Version),
            LexicalAnalyzerName.Values.HyLucene => new ArmenianAnalyzer(Version),
            LexicalAnalyzerName.Values.IdLucene => new IndonesianAnalyzer(Version),
            LexicalAnalyzerName.Values.ItLucene => new ItalianAnalyzer(Version),
            LexicalAnalyzerName.Values.LvLucene => new LatvianAnalyzer(Version),
            LexicalAnalyzerName.Values.NlLucene => new DutchAnalyzer(Version),
            LexicalAnalyzerName.Values.NoLucene => new NorwegianAnalyzer(Version),
            LexicalAnalyzerName.Values.PtBrLucene => new BrazilianAnalyzer(Version),
            LexicalAnalyzerName.Values.PtPtLucene => new PortugueseAnalyzer(Version),
            LexicalAnalyzerName.Values.RoLucene => new RomanianAnalyzer(Version),
            LexicalAnalyzerName.Values.RuLucene => new RussianAnalyzer(Version),
            LexicalAnalyzerName.Values.SvLucene => new SwedishAnalyzer(Version),
            LexicalAnalyzerName.Values.TrLucene => new TurkishAnalyzer(Version),
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