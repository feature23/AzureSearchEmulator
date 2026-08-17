using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Shared query-building and validation for <c>docs/suggest</c> and <c>docs/autocomplete</c>
/// (issue #45).
/// </summary>
/// <remarks>
/// Azure Search answers both from an edge n-gram side index built at index time, so a partial
/// word is a plain term lookup there. The emulator has no such side index, and building one
/// would mean re-indexing every document whenever a suggester is added. A prefix query over
/// the suggester's source fields reaches the same documents at query time, which the issue
/// notes is close enough for emulation.
///
/// The gap that leaves is scoring: Azure ranks suggestions by its own n-gram relevance, so the
/// <em>order</em> of two equally-matching suggestions may differ from the real service even
/// though the set does not. Callers that assert on set membership behave the same locally;
/// callers that assert on exact ranking may not.
/// </remarks>
public static class SuggesterSupport
{
    /// <summary>
    /// Azure Search rejects a <c>$top</c> above this on both suggest and autocomplete.
    /// </summary>
    public const int MaxTop = 100;

    /// <summary>
    /// The edit distance Azure Search allows when <c>fuzzy</c> is set.
    /// </summary>
    /// <remarks>
    /// Azure documents fuzzy suggestions as tolerating a single misspelled character, which is
    /// one edit rather than Lucene's default of two.
    /// </remarks>
    private const int FuzzyMaxEdits = 1;

    /// <summary>
    /// Resolves the source fields a request may match against, or throws when the request
    /// names a suggester or field the index cannot satisfy.
    /// </summary>
    /// <remarks>
    /// <paramref name="searchFields"/> narrows the suggester's own source fields rather than
    /// widening them: Azure Search treats a field outside the suggester as an error, because
    /// no n-grams were built for it. Refusing here keeps that same boundary visible instead of
    /// quietly returning suggestions the real service would not.
    /// </remarks>
    public static IReadOnlyList<string> ResolveSourceFields(
        SearchIndex index,
        string? suggesterName,
        string? searchFields)
    {
        if (string.IsNullOrWhiteSpace(suggesterName))
        {
            throw new InvalidOperationException("The suggesterName parameter is required.");
        }

        var suggester = index.FindSuggester(suggesterName);

        if (suggester == null)
        {
            var known = index.Suggesters.Count == 0
                ? "the index defines no suggesters"
                : $"known suggesters: {string.Join(", ", index.Suggesters.Select(i => i.Name))}";

            throw new InvalidOperationException(
                $"The index '{index.Name}' does not have a suggester named '{suggesterName}'; {known}.");
        }

        // Resolved through the schema so that a source field named in any casing is queried
        // under the spelling it was indexed with; Lucene field names are case-sensitive.
        var resolved = new List<string>(suggester.SourceFields.Count);

        foreach (var sourceField in suggester.SourceFields)
        {
            if (!ComplexTypeSupport.TryResolvePath(index, sourceField, out var field, out var path))
            {
                throw new InvalidOperationException(
                    $"The suggester '{suggester.Name}' names field '{sourceField}', which does not exist in the index '{index.Name}'.");
            }

            if (!field.Searchable.GetValueOrDefault())
            {
                throw new InvalidOperationException(
                    $"The suggester '{suggester.Name}' names field '{sourceField}', which is not searchable.");
            }

            resolved.Add(path);
        }

        if (resolved.Count == 0)
        {
            throw new InvalidOperationException(
                $"The suggester '{suggester.Name}' has no source fields.");
        }

        if (string.IsNullOrWhiteSpace(searchFields))
        {
            return resolved;
        }

        var requested = searchFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var narrowed = new List<string>(requested.Length);

        foreach (var requestedField in requested)
        {
            var match = resolved.FirstOrDefault(i => i.Equals(requestedField, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                throw new InvalidOperationException(
                    $"The searchFields parameter names field '{requestedField}', which is not a source field of the suggester '{suggester.Name}'.");
            }

            narrowed.Add(match);
        }

        return narrowed;
    }

    /// <summary>
    /// Builds the query that finds documents whose source fields match the caller's partial
    /// search text.
    /// </summary>
    /// <remarks>
    /// The text is split on whitespace: every term but the last is complete and must match as
    /// written, while the last is the one still being typed and matches as a prefix. That
    /// mirrors what a typeahead box sends on each keystroke.
    ///
    /// Terms are lowercased because the source fields are analyzed at index time by an
    /// analyzer that lowercases, and neither a prefix nor a fuzzy query runs its input through
    /// that analyzer — an unlowercased "Lap" would find nothing against an indexed "laptop".
    ///
    /// Returns null when the search text carries no terms at all, which cannot match anything.
    /// </remarks>
    public static Query? BuildQuery(string? search, IReadOnlyList<string> sourceFields, bool fuzzy)
    {
        var terms = Tokenize(search);

        if (terms.Count == 0)
        {
            return null;
        }

        var completeTerms = terms.Take(terms.Count - 1);
        var partialTerm = terms[^1];

        // Each term must be present, but may be found in any one of the source fields, so the
        // per-term clause is a disjunction and the terms themselves are conjoined.
        var query = new BooleanQuery();

        foreach (var term in completeTerms)
        {
            query.Add(BuildTermClause(term, sourceFields, fuzzy, prefix: false), Occur.MUST);
        }

        query.Add(BuildTermClause(partialTerm, sourceFields, fuzzy, prefix: true), Occur.MUST);

        return query;
    }

    /// <summary>
    /// Builds the "this term appears in at least one source field" clause.
    /// </summary>
    private static Query BuildTermClause(string term, IReadOnlyList<string> sourceFields, bool fuzzy, bool prefix)
    {
        var clause = new BooleanQuery();

        foreach (var field in sourceFields)
        {
            var luceneTerm = new Term(field, term);

            // A fuzzy query already tolerates the missing characters of a half-typed word only
            // up to its edit distance, so the partial term keeps its prefix behaviour and
            // fuzziness applies to the terms the caller finished typing.
            Query fieldQuery = prefix
                ? new PrefixQuery(luceneTerm)
                : fuzzy
                    ? new FuzzyQuery(luceneTerm, FuzzyMaxEdits)
                    : new TermQuery(luceneTerm);

            clause.Add(fieldQuery, Occur.SHOULD);
        }

        return clause;
    }

    /// <summary>
    /// Splits search text into the lowercased terms a query is built from.
    /// </summary>
    public static List<string> Tokenize(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        return search
            .Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(i => i.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// The characters a caller's search text is split on.
    /// </summary>
    /// <remarks>
    /// Punctuation is included because the standard analyzer strips it at index time, so a
    /// term still carrying a trailing comma would match nothing.
    /// </remarks>
    private static readonly char[] TermSeparators =
        [' ', '\t', '\n', '\r', ',', ';', ':', '.', '!', '?', '"', '(', ')', '[', ']', '{', '}'];

    /// <summary>
    /// Validates the <c>$top</c> a suggest or autocomplete request asked for.
    /// </summary>
    public static string? ValidateTop(int top)
        => top is > MaxTop or < 1
            ? $"Top must be between 1 and {MaxTop}"
            : null;
}
