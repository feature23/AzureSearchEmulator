using System.Text.RegularExpressions;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// The default semantic scorer: the share of the query's words a passage contains.
/// </summary>
/// <remarks>
/// This is not semantic in any sense, and it is not meant to be. It gives a deterministic score
/// with nothing to download, so that an app sending semantic queries gets a usable, well-formed
/// response. <see cref="CrossEncoderSemanticScorer"/> is the opt-in for an order that means
/// something.
/// </remarks>
public sealed partial class LexicalSemanticScorer : ISemanticScorer
{
    /// <summary>
    /// Words too common to say anything about relevance. Without these a question such as "what
    /// is the capital of France" would score well against almost any English passage.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how",
        "i", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "was", "what", "when",
        "where", "which", "who", "why", "will", "with",
    };

    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> passages)
    {
        var queryTerms = GetQueryTerms(query);

        var scores = new double[passages.Count];

        if (queryTerms.Count == 0)
        {
            return scores;
        }

        for (var i = 0; i < passages.Count; i++)
        {
            var words = Tokenize(passages[i]);
            var present = new HashSet<string>(words.Where(queryTerms.Contains), StringComparer.Ordinal);
            var occurrences = words.Count(queryTerms.Contains);

            // Mostly coverage, so a passage with every query word beats one repeating a single
            // word; the small occurrence term breaks ties between passages covering the same words.
            scores[i] = 0.9 * present.Count / queryTerms.Count + 0.1 * Math.Min(1.0, occurrences / 5.0);
        }

        return scores;
    }

    /// <summary>
    /// The distinct words of a query that bear on relevance, lower-cased.
    /// </summary>
    /// <remarks>
    /// A query made only of stop words keeps them, since leaving it with no terms at all would
    /// score every passage zero.
    /// </remarks>
    public static HashSet<string> GetQueryTerms(string query)
    {
        var words = Tokenize(query);
        var terms = new HashSet<string>(words.Where(w => !StopWords.Contains(w)), StringComparer.Ordinal);

        return terms.Count > 0 ? terms : new HashSet<string>(words, StringComparer.Ordinal);
    }

    /// <summary>
    /// Wraps every word of <paramref name="text"/> that is one of the query's terms in the
    /// highlight tags.
    /// </summary>
    public static string Highlight(string text, HashSet<string> queryTerms, string preTag = "<em>", string postTag = "</em>")
        => WordPattern().Replace(text, m =>
            queryTerms.Contains(m.Value.ToLowerInvariant()) ? preTag + m.Value + postTag : m.Value);

    private static List<string> Tokenize(string text)
        => WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

    [GeneratedRegex(@"\w+")]
    private static partial Regex WordPattern();
}
