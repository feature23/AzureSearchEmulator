using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Highlight;

namespace AzureSearchEmulator.Searching;

public class HitHighlighter
{
    private readonly Highlighter _highlighter;
    private readonly SearchIndex _index;

    /// <param name="index">
    /// The index the highlighted fields belong to, needed so that a field analyzed by one of
    /// the index's own custom analyzers is re-tokenized with that analyzer rather than the
    /// default (issue #34). Highlighting compares the query's terms against the tokens of the
    /// stored text, so using a different analyzer here than the field was indexed with would
    /// mark the wrong spans, or none.
    /// </param>
    public HitHighlighter(SearchIndex index, Query query, string preTag, string postTag, IList<HighlightField> fields)
    {
        _index = index;
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

            var tokenStream = TokenSources.GetAnyTokenStream(reader, docId, path, doc, AnalyzerHelper.GetAnalyzer(_index, field.SearchAnalyzer ?? field.Analyzer));
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