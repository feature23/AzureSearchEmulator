using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Synonym;
using Lucene.Net.Util;
using LuceneSynonymMap = Lucene.Net.Analysis.Synonym.SynonymMap;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Turns a synonym map's Solr rules into the compiled form Lucene matches against (issue #69).
/// </summary>
public static class SynonymMapBuilder
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    /// <summary>
    /// Parses the map's rules.
    /// </summary>
    /// <remarks>
    /// The parser is given <c>expand: true</c> so that a comma-separated rule makes every term
    /// equivalent to every other, which is what Azure documents an equivalency rule to mean. It
    /// does not affect the explicit <c>a =&gt; b</c> form: an arrow rule always replaces its left
    /// side, so <c>dog =&gt; canine</c> drops "dog" from the query whichever way expand is set.
    ///
    /// Rules are tokenized with the same analyzer the terms will be matched against a
    /// lower-casing whitespace chain rather than the field's own. Azure matches synonyms on
    /// whole words, and using the field's analyzer here would let a stemming or n-gram chain
    /// rewrite the rule text into tokens the rule's author never wrote.
    /// </remarks>
    /// <exception cref="SynonymMapDefinitionException">
    /// The rules are malformed. <see cref="Indexing.SynonymMapValidator"/> refuses those when
    /// the map is created, so reaching this means a map was written before that validation
    /// existed.
    /// </exception>
    public static LuceneSynonymMap Build(SynonymMap synonymMap)
    {
        if (!string.Equals(synonymMap.Format, SynonymMap.SolrFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new SynonymMapDefinitionException(
                $"Synonym map '{synonymMap.Name}' declares format '{synonymMap.Format}', but only " +
                $"'{SynonymMap.SolrFormat}' is supported.");
        }

        var parser = new SolrSynonymParser(dedup: true, expand: true, analyzer: CreateRuleAnalyzer());

        try
        {
            parser.Parse(new StringReader(synonymMap.Synonyms));

            return parser.Build();
        }
        catch (Exception ex) when (ex is not SynonymMapDefinitionException)
        {
            throw new SynonymMapDefinitionException(
                $"Synonym map '{synonymMap.Name}' could not be parsed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The analyzer the rule text itself is read with.
    /// </summary>
    /// <remarks>
    /// Whitespace-split and lower-cased, so a multi-word rule such as <c>united states</c>
    /// becomes the two tokens the filter needs to match in sequence, and the map is
    /// case-insensitive the way Azure's is.
    /// </remarks>
    private static Analyzer CreateRuleAnalyzer() =>
        Analyzer.NewAnonymous((_, reader) =>
        {
            Tokenizer source = new WhitespaceTokenizer(Version, reader);

            return new TokenStreamComponents(source, new LowerCaseFilter(Version, source));
        });
}

/// <summary>
/// Raised when a synonym map's declared format or rules cannot be turned into a usable map.
/// </summary>
public class SynonymMapDefinitionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
