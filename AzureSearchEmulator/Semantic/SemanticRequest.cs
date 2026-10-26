using System.Globalization;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Searching;

namespace AzureSearchEmulator.Semantic;

/// <summary>
/// What a <c>queryType=semantic</c> request asks of the ranker, parsed and validated against the
/// index before anything is searched.
/// </summary>
public sealed class SemanticRequest
{
    public const string QueryType = "semantic";

    private const string Extractive = "extractive";

    private const int MaxAnswers = 10;

    public required SemanticConfiguration Configuration { get; init; }

    /// <summary>
    /// The text the ranker scores against: <c>semanticQuery</c> when given, which Azure offers so
    /// retrieval and reranking can use different text, and otherwise the search text.
    /// </summary>
    public string? QueryText { get; init; }

    public AnswerOptions? Answers { get; init; }

    public CaptionOptions? Captions { get; init; }

    public sealed record AnswerOptions(int Count, double Threshold, int? MaxCharLength);

    public sealed record CaptionOptions(bool Highlight, int? MaxCharLength);

    /// <summary>
    /// Returns null for a request that is not semantic.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The semantic configuration cannot be resolved, or <c>answers</c> or <c>captions</c> is
    /// malformed.
    /// </exception>
    public static SemanticRequest? Parse(SearchIndex index, SearchRequest request)
    {
        if (!string.Equals(request.QueryType, QueryType, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var queryText = string.IsNullOrWhiteSpace(request.SemanticQuery) ? request.Search : request.SemanticQuery;

        return new SemanticRequest
        {
            Configuration = SemanticConfiguration.Resolve(index, request.SemanticConfiguration),
            QueryText = queryText is null or "*" ? null : queryText,
            Answers = ParseAnswers(request.Answers),
            Captions = ParseCaptions(request.Captions),
        };
    }

    /// <summary>
    /// Parses <c>extractive</c>, optionally followed by <c>|</c> and a comma-separated list of
    /// <c>count-N</c>, <c>threshold-X</c> and <c>maxcharlength-N</c>.
    /// </summary>
    private static AnswerOptions? ParseAnswers(string? value)
    {
        if (!TryParseExtractive(value, "answers", out var options))
        {
            return null;
        }

        var count = 1;
        var threshold = 0.7;
        int? maxCharLength = null;

        foreach (var (name, argument) in options)
        {
            switch (name)
            {
                case "count" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                                  && c is >= 1 and <= MaxAnswers:
                    count = c;
                    break;
                case "threshold" when double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                                      && t is >= 0 and <= 1:
                    threshold = t;
                    break;
                case "maxcharlength" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0:
                    maxCharLength = m;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Invalid answers option '{name}-{argument}'. Expected count-<1-{MaxAnswers}>, threshold-<0-1> or maxcharlength-<n>.");
            }
        }

        return new AnswerOptions(count, threshold, maxCharLength);
    }

    /// <summary>
    /// Parses <c>extractive</c>, optionally followed by <c>|</c> and a comma-separated list of
    /// <c>highlight-true|false</c> and <c>maxcharlength-N</c>. Highlighting is on by default.
    /// </summary>
    private static CaptionOptions? ParseCaptions(string? value)
    {
        if (!TryParseExtractive(value, "captions", out var options))
        {
            return null;
        }

        var highlight = true;
        int? maxCharLength = null;

        foreach (var (name, argument) in options)
        {
            switch (name)
            {
                case "highlight" when bool.TryParse(argument, out var h):
                    highlight = h;
                    break;
                case "maxcharlength" when int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0:
                    maxCharLength = m;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Invalid captions option '{name}-{argument}'. Expected highlight-<true|false> or maxcharlength-<n>.");
            }
        }

        return new CaptionOptions(highlight, maxCharLength);
    }

    /// <returns>False for an absent value or <c>none</c>.</returns>
    private static bool TryParseExtractive(string? value, string parameter, out List<(string Name, string Argument)> options)
    {
        options = [];

        if (string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);

        if (!parts[0].Equals(Extractive, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Invalid {parameter} value '{value}'. Expected 'none' or 'extractive'.");
        }

        if (parts.Length == 1)
        {
            return true;
        }

        foreach (var option in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = option.IndexOf('-');

            options.Add(dash < 0
                ? (option.ToLowerInvariant(), "")
                : (option[..dash].ToLowerInvariant(), option[(dash + 1)..]));
        }

        return true;
    }
}
