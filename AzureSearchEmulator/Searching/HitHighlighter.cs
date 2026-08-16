using AzureSearchEmulator.SearchData;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;

namespace AzureSearchEmulator.Searching;

public class HitHighlighter
{
    private readonly Highlighter _highlighter;

    public HitHighlighter(Query query, string preTag, string postTag, IList<HighlightField> fields)
    {
        Fields = fields;
        var formatter = new SimpleHTMLFormatter(preTag, postTag);
        _highlighter = new Highlighter(formatter, new QueryScorer(query));
    }

    public IList<HighlightField> Fields { get; }

    public IDictionary<string, IList<string>> GetHighlights(IndexReader reader, int docId, Document doc)
    {
        var results = new Dictionary<string, IList<string>>();

        foreach (var (field, maxHighlights, path) in Fields)
        {
            var text = doc.Get(path);

            if (string.IsNullOrEmpty(text))
                continue;

            var tokenStream = TokenSources.GetAnyTokenStream(reader, docId, path, doc, AnalyzerHelper.GetAnalyzer(field.SearchAnalyzer ?? field.Analyzer));
            var textFragments = _highlighter.GetBestTextFragments(tokenStream, text, false, maxHighlights);

            var fieldHighlights = (from textFragment in textFragments
                where textFragment is { Score: > 0 }
                select textFragment.ToString()).ToList();

            if (fieldHighlights.Count > 0)
            {
                results[path] = fieldHighlights;
            }
        }

        return results;
    }
}