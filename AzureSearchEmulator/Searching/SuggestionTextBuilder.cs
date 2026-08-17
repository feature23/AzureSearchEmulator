using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Turns a matched document's stored field text into the <c>@search.text</c> a suggestion
/// carries, and into the completions autocomplete returns (issue #45).
/// </summary>
public static class SuggestionTextBuilder
{
    /// <summary>
    /// Picks the text a suggestion shows: the first source field of the document that actually
    /// matched the caller's terms.
    /// </summary>
    /// <remarks>
    /// Azure returns the content of the field the n-grams matched in, not the whole document,
    /// so a document matching on its Name shows the Name rather than its Description. The
    /// source fields are checked in the order the suggester declares them, which is the order
    /// Azure's own precedence follows.
    ///
    /// Returns null when no source field of this document contains the terms — a document the
    /// query reached through a field that is stored differently than it is indexed, which
    /// should not produce a suggestion with empty text.
    ///
    /// Under <paramref name="fuzzy"/> the terms are deliberately misspelled, so looking for
    /// them literally would reject every field of a document the query legitimately matched.
    /// The query is the authority on what matched in that case, and the first populated source
    /// field is used; the exact check still runs first, so a document that does contain the
    /// terms as typed still shows the field that holds them rather than merely the first one.
    /// </remarks>
    public static string? GetSuggestionText(
        IReadOnlyList<string> sourceFields,
        Func<string, string?> getFieldText,
        IReadOnlyList<string> terms,
        bool fuzzy = false)
    {
        string? firstPopulated = null;

        foreach (var field in sourceFields)
        {
            var text = getFieldText(field);

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (ContainsAllTerms(text, terms))
            {
                return text;
            }

            firstPopulated ??= text;
        }

        return fuzzy ? firstPopulated : null;
    }

    /// <summary>
    /// Wraps the matched portion of a suggestion in the caller's highlight tags.
    /// </summary>
    /// <remarks>
    /// Only the terms the caller actually typed are wrapped, and the final one is wrapped as
    /// the prefix it is — typing "lap" highlights just the "lap" of "laptop", which is what
    /// makes a typeahead list show what the user has matched so far. When no tags are given,
    /// the text is returned unchanged rather than wrapped in a default, because Azure omits
    /// highlighting from a suggestion unless it is asked for.
    /// </remarks>
    public static string ApplyHighlighting(
        string text,
        IReadOnlyList<string> terms,
        string? preTag,
        string? postTag)
    {
        if (string.IsNullOrEmpty(preTag) && string.IsNullOrEmpty(postTag))
        {
            return text;
        }

        preTag ??= "";
        postTag ??= "";

        var result = new System.Text.StringBuilder(text.Length);
        var position = 0;

        foreach (var (start, length) in FindTermSpans(text, terms))
        {
            result.Append(text, position, start - position);
            result.Append(preTag);
            result.Append(text, start, length);
            result.Append(postTag);
            position = start + length;
        }

        result.Append(text, position, text.Length - position);

        return result.ToString();
    }

    /// <summary>
    /// Builds the completions for a <c>docs/autocomplete</c> call from a matched document's
    /// text.
    /// </summary>
    /// <remarks>
    /// Autocomplete completes the term the caller is still typing, so the word the prefix
    /// landed in is what comes back — "lap" against "Laptop Pro 15" completes to "laptop".
    /// Under <c>twoTerms</c> the word after it is appended when there is one, giving the
    /// caller a two-word phrase that actually occurs in the corpus.
    ///
    /// <c>oneTermWithContext</c> completes a single term as <c>oneTerm</c> does, but only
    /// where the preceding words the caller typed are the words that precede it in the
    /// document, so the completion is one the corpus supports in that exact context. With
    /// nothing typed before the partial term the two modes coincide, which is what
    /// <see cref="GetContextualCompletions"/> falls back to.
    /// </remarks>
    public static IEnumerable<string> GetCompletions(string text, string partialTerm, string mode)
    {
        var words = SplitWords(text);

        for (var i = 0; i < words.Count; i++)
        {
            if (!words[i].StartsWith(partialTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (mode.Equals(AutocompleteModes.TwoTerms, StringComparison.OrdinalIgnoreCase)
                && i + 1 < words.Count)
            {
                yield return $"{words[i]} {words[i + 1]}".ToLowerInvariant();
                continue;
            }

            yield return words[i].ToLowerInvariant();
        }
    }

    /// <summary>
    /// The <c>oneTermWithContext</c> completions: the partial term completed only where the
    /// words the caller already typed immediately precede it in the text.
    /// </summary>
    public static IEnumerable<string> GetContextualCompletions(
        string text,
        IReadOnlyList<string> completeTerms,
        string partialTerm)
    {
        if (completeTerms.Count == 0)
        {
            return GetCompletions(text, partialTerm, AutocompleteModes.OneTerm);
        }

        return GetContextualCompletionsCore(text, completeTerms, partialTerm);
    }

    private static IEnumerable<string> GetContextualCompletionsCore(
        string text,
        IReadOnlyList<string> completeTerms,
        string partialTerm)
    {
        var words = SplitWords(text);

        // The partial term can only start where there is room for the context words ahead of
        // it, so the scan begins past them.
        for (var i = completeTerms.Count; i < words.Count; i++)
        {
            if (!words[i].StartsWith(partialTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var contextMatches = true;

            for (var j = 0; j < completeTerms.Count; j++)
            {
                var contextWord = words[i - completeTerms.Count + j];

                if (!contextWord.Equals(completeTerms[j], StringComparison.OrdinalIgnoreCase))
                {
                    contextMatches = false;
                    break;
                }
            }

            if (contextMatches)
            {
                yield return words[i].ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Finds the non-overlapping spans of <paramref name="text"/> the caller's terms matched,
    /// in the order they occur.
    /// </summary>
    /// <remarks>
    /// Every term but the last must match a whole word, while the last matches as a prefix,
    /// mirroring how <see cref="SuggesterSupport.BuildQuery"/> treats them. Spans are sorted
    /// and de-overlapped so the tags nest correctly when two terms land in the same word.
    /// </remarks>
    private static IEnumerable<(int Start, int Length)> FindTermSpans(string text, IReadOnlyList<string> terms)
    {
        var spans = new List<(int Start, int Length)>();

        for (var i = 0; i < terms.Count; i++)
        {
            var isPartial = i == terms.Count - 1;

            foreach (var (start, length) in FindWordSpans(text))
            {
                var word = text.AsSpan(start, length);

                if (isPartial)
                {
                    if (word.StartsWith(terms[i], StringComparison.OrdinalIgnoreCase))
                    {
                        // Only the typed prefix is wrapped, not the rest of the word.
                        spans.Add((start, terms[i].Length));
                    }
                }
                else if (word.Equals(terms[i], StringComparison.OrdinalIgnoreCase))
                {
                    spans.Add((start, length));
                }
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var lastEnd = 0;

        foreach (var span in spans)
        {
            if (span.Start >= lastEnd)
            {
                yield return span;
                lastEnd = span.Start + span.Length;
            }
        }
    }

    private static bool ContainsAllTerms(string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return false;
        }

        var words = SplitWords(text);

        for (var i = 0; i < terms.Count; i++)
        {
            // The last term is the one still being typed, so a prefix of a word satisfies it.
            var isPartial = i == terms.Count - 1;

            var found = words.Any(word => isPartial
                ? word.StartsWith(terms[i], StringComparison.OrdinalIgnoreCase)
                : word.Equals(terms[i], StringComparison.OrdinalIgnoreCase));

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits field text into words, using the same separators a query's terms were split on
    /// so that the two agree on what a word is.
    /// </summary>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();

        foreach (var (start, length) in FindWordSpans(text))
        {
            words.Add(text.Substring(start, length));
        }

        return words;
    }

    /// <summary>
    /// Locates each word of <paramref name="text"/> by position, so highlighting can splice
    /// tags into the original string rather than reassembling it from tokens and losing its
    /// punctuation and spacing.
    /// </summary>
    private static IEnumerable<(int Start, int Length)> FindWordSpans(string text)
    {
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            // Kept consistent with the query side: a word is a run of letters and digits, and
            // everything the standard analyzer strips is a boundary.
            if (char.IsLetterOrDigit(text[i]))
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                yield return (start, i - start);
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return (start, text.Length - start);
        }
    }
}
