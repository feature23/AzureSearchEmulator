using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// A retrieved document awaiting reranking.
/// </summary>
/// <param name="Result">The result as it will be returned, already narrowed by <c>$select</c>.</param>
/// <param name="Document">
/// Every retrievable field, because the semantic configuration's fields need not be selected
/// for the ranker to read them.
/// </param>
/// <param name="Score">The retrieval score, which breaks ties between equal reranker scores.</param>
public sealed record SemanticCandidate(JsonObject Result, JsonObject Document, double Score);

public sealed record SemanticAnswer(string Key, string Text, string? Highlights, double Score);

/// <summary>
/// Reranks retrieved documents and extracts captions and answers, imitating the second stage of
/// an Azure semantic query (issue #109).
/// </summary>
/// <remarks>
/// <para>
/// As in Azure, only the first <see cref="MaxRerankedResults"/> retrieved documents are reranked,
/// and they are returned ahead of any others, which keep their retrieval order and carry no
/// <c>@search.rerankerScore</c>.
/// </para>
/// <para>
/// The scores are the <see cref="ISemanticScorer"/>'s relevance scaled onto Azure's 0–4 range,
/// so code that thresholds on the reranker score sees values of the right magnitude. Captions
/// and answers are extractive, as Azure's are: the best-scoring passage of each document, with
/// the query's words highlighted.
/// </para>
/// </remarks>
public sealed partial class SemanticReranker(ISemanticScorer scorer)
{
    public const int MaxRerankedResults = 50;

    /// <summary>
    /// How many of the top reranked documents answers are drawn from.
    /// </summary>
    private const int AnswerSourceDocuments = 5;

    /// <summary>
    /// Passages scored per document for a caption, bounding the cost of a long document.
    /// </summary>
    private const int MaxPassagesPerDocument = 16;

    /// <summary>
    /// The longest passage in words. A field with no sentence punctuation would otherwise become
    /// one passage, and the caption would be the whole field.
    /// </summary>
    private const int MaxPassageWords = 50;

    public (IReadOnlyList<JsonObject> Results, IReadOnlyList<SemanticAnswer>? Answers) Rerank(
        SearchIndex index,
        SemanticRequest semantic,
        IReadOnlyList<SemanticCandidate> candidates,
        int skip,
        int top)
    {
        IReadOnlyList<SemanticAnswer>? noAnswers = semantic.Answers == null ? null : [];

        // Nothing to rank against: an empty or wildcard search with no semanticQuery.
        if (semantic.QueryText is not { } query)
        {
            return (candidates.Skip(skip).Take(top).Select(c => c.Result).ToList(), noAnswers);
        }

        var configuration = semantic.Configuration;
        var reranked = candidates.Take(MaxRerankedResults).ToList();
        var relevance = scorer.Score(query, reranked.Select(c => GetRankingText(c.Document, configuration)).ToList());

        for (var i = 0; i < reranked.Count; i++)
        {
            reranked[i].Result["@search.rerankerScore"] = Math.Round(relevance[i] * 4, 6);
        }

        var ordered = reranked
            .Select((candidate, i) => (Candidate: candidate, Relevance: relevance[i]))
            .OrderByDescending(r => r.Relevance)
            .ThenByDescending(r => r.Candidate.Score)
            .Select(r => r.Candidate)
            .Concat(candidates.Skip(MaxRerankedResults))
            .ToList();

        var queryTerms = LexicalSemanticScorer.GetQueryTerms(query);
        var bestPassages = new Dictionary<SemanticCandidate, (string Text, double Score)?>();

        (string Text, double Score)? GetBestPassage(SemanticCandidate candidate)
        {
            if (!bestPassages.TryGetValue(candidate, out var passage))
            {
                passage = FindBestPassage(query, candidate.Document, configuration);
                bestPassages[candidate] = passage;
            }

            return passage;
        }

        var page = ordered.Skip(skip).Take(top).ToList();

        if (semantic.Captions is { } captions)
        {
            foreach (var candidate in page)
            {
                var caption = new JsonArray();

                if (GetBestPassage(candidate) is { } passage)
                {
                    var text = Truncate(passage.Text, captions.MaxCharLength);
                    var item = new JsonObject { ["text"] = text };

                    if (captions.Highlight)
                    {
                        item["highlights"] = LexicalSemanticScorer.Highlight(text, queryTerms);
                    }

                    caption.Add(item);
                }

                candidate.Result["@search.captions"] = caption;
            }
        }

        IReadOnlyList<SemanticAnswer>? answers = null;

        if (semantic.Answers is { } answerOptions)
        {
            var keyField = index.GetKeyField().Name;

            answers = ordered
                .Take(Math.Min(AnswerSourceDocuments, reranked.Count))
                .Select(candidate => (Candidate: candidate, Passage: GetBestPassage(candidate)))
                .Where(a => a.Passage is { } p && p.Score >= answerOptions.Threshold)
                .OrderByDescending(a => a.Passage!.Value.Score)
                .Take(answerOptions.Count)
                .Select(a =>
                {
                    var text = Truncate(a.Passage!.Value.Text, answerOptions.MaxCharLength);

                    return new SemanticAnswer(
                        a.Candidate.Document[keyField]?.ToString() ?? "",
                        text,
                        LexicalSemanticScorer.Highlight(text, queryTerms),
                        Math.Round(a.Passage.Value.Score, 6));
                })
                .ToList();
        }

        return (page.Select(c => c.Result).ToList(), answers);
    }

    /// <summary>
    /// The document as the ranker reads it: title, then content, then keywords, as Azure orders
    /// the prioritized fields.
    /// </summary>
    private static string GetRankingText(JsonObject document, SemanticConfiguration configuration)
    {
        var fields = new List<string>();

        if (configuration.TitleField != null)
        {
            fields.Add(configuration.TitleField);
        }

        fields.AddRange(configuration.ContentFields);
        fields.AddRange(configuration.KeywordsFields);

        return string.Join("\n", fields.SelectMany(f => GetText(document, f)));
    }

    private (string Text, double Score)? FindBestPassage(string query, JsonObject document, SemanticConfiguration configuration)
    {
        // Content first; the title only for a document whose content fields are empty.
        var passages = configuration.ContentFields
            .SelectMany(f => GetText(document, f))
            .SelectMany(SplitPassages)
            .Take(MaxPassagesPerDocument)
            .ToList();

        if (passages.Count == 0 && configuration.TitleField != null)
        {
            passages = GetText(document, configuration.TitleField).ToList();
        }

        if (passages.Count == 0)
        {
            return null;
        }

        var scores = scorer.Score(query, passages);
        var best = 0;

        for (var i = 1; i < scores.Count; i++)
        {
            if (scores[i] > scores[best])
            {
                best = i;
            }
        }

        return (passages[best], scores[best]);
    }

    /// <summary>
    /// The string values at a field path such as <c>Address/City</c>, flattening collections.
    /// </summary>
    private static IEnumerable<string> GetText(JsonObject document, string path)
    {
        IEnumerable<JsonNode?> nodes = [document];

        foreach (var segment in path.Split('/'))
        {
            nodes = nodes
                .SelectMany(Flatten)
                .Select(n => n is JsonObject obj ? obj[segment] : null);
        }

        return nodes
            .SelectMany(Flatten)
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim());
    }

    /// <remarks>
    /// Not written as <c>node is JsonArray a ? a : [node]</c>: the conditional takes the type of
    /// its <see cref="JsonArray"/> branch, so the collection expression builds a new JsonArray,
    /// which throws on adopting a node that already belongs to the document.
    /// </remarks>
    private static IEnumerable<JsonNode?> Flatten(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        return [node];
    }

    private static IEnumerable<string> SplitPassages(string text)
    {
        foreach (var sentence in SentenceBoundary().Split(text))
        {
            var words = sentence.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            for (var start = 0; start < words.Length; start += MaxPassageWords)
            {
                yield return string.Join(' ', words.Skip(start).Take(MaxPassageWords));
            }
        }
    }

    /// <summary>
    /// Cuts at the last word boundary within the limit, so a caption never ends mid-word.
    /// </summary>
    private static string Truncate(string text, int? maxLength)
    {
        if (maxLength is not { } max || text.Length <= max)
        {
            return text;
        }

        var space = text.LastIndexOf(' ', max);

        return text[..(space > 0 ? space : max)];
    }

    [GeneratedRegex(@"(?<=[.!?])\s+|\r?\n+")]
    private static partial Regex SentenceBoundary();
}
