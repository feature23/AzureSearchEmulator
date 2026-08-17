using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Resolves which scoring profile a request runs under, and checks the request carries what the
/// profile's functions need (issue #47).
/// </summary>
public static class ScoringProfileSupport
{
    /// <summary>
    /// The profile a request runs under: the one it named, else the index's default, else none.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The request named a profile the index does not define.
    /// </exception>
    public static ScoringProfile? Resolve(SearchIndex index, string? requested)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            return index.FindScoringProfile(requested)
                   ?? throw new InvalidOperationException(
                       $"The index '{index.Name}' does not have a scoring profile named '{requested}'.");
        }

        // A default naming a missing profile is refused when the index is defined, so an
        // unresolvable name here can only come from a definition written before that check.
        return string.IsNullOrEmpty(index.DefaultScoringProfile)
            ? null
            : index.FindScoringProfile(index.DefaultScoringProfile);
    }

    /// <summary>
    /// Returns a message naming every scoring parameter the profile's functions need and the
    /// request did not supply, or null when they are all present.
    /// </summary>
    /// <remarks>
    /// Azure refuses such a query rather than running it unboosted — "Expected 1 parameter(s)
    /// but 0 were supplied" — and the emulator does the same, for the reason the unsupported
    /// parameters in issue #39 are refused: a query that silently loses its boosting ranks
    /// differently from the same query against the real service, with nothing to say why.
    ///
    /// Only <c>distance</c> and <c>tag</c> take a parameter; <c>magnitude</c> and
    /// <c>freshness</c> are defined entirely by the index.
    /// </remarks>
    public static string? GetMissingParameterMessage(
        ScoringProfile profile,
        ScoringParameterCollection parameters)
    {
        var missing = new List<string>();

        foreach (var function in profile.Functions)
        {
            var name = function switch
            {
                DistanceScoringFunction distance => distance.Distance?.ReferencePointParameter,
                TagScoringFunction tag => tag.Tag?.TagsParameter,
                _ => null,
            };

            if (name != null && parameters.GetValues(name) == null && !missing.Contains(name))
            {
                missing.Add(name);
            }
        }

        if (missing.Count == 0)
        {
            return null;
        }

        return $"The scoring profile '{profile.Name}' requires the scoring " +
               $"{(missing.Count == 1 ? "parameter" : "parameters")} {string.Join(", ", missing)}, " +
               $"which the request did not supply. Pass {(missing.Count == 1 ? "it" : "them")} as " +
               "scoringParameter values in the form 'name-value'.";
    }

    /// <summary>
    /// The text weights a profile applies, keyed by the canonical field path so they line up
    /// with the fields the query parser is given.
    /// </summary>
    /// <remarks>
    /// A profile names its fields in whatever casing its author used, while Lucene field names
    /// are case-sensitive, so each name is resolved through the schema before being used as a
    /// weight key — the same rule <c>searchFields</c> follows.
    ///
    /// Weights for fields outside the ones being searched are dropped rather than added: a
    /// weight is an adjustment to a field's contribution, so it cannot bring a field into a
    /// query that was not searching it.
    /// </remarks>
    public static IDictionary<string, float> GetWeights(
        SearchIndex index,
        ScoringProfile? profile,
        IEnumerable<string> searchFields)
    {
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var path in searchFields)
        {
            weights[path] = 1.0f;
        }

        foreach (var (name, weight) in profile?.Text?.Weights ?? [])
        {
            if (ComplexTypeSupport.TryResolvePath(index, name, out _, out var path)
                && weights.ContainsKey(path))
            {
                weights[path] = (float)weight;
            }
        }

        return weights;
    }

    /// <summary>
    /// Applies a profile's text weights to an already-parsed query, boosting each clause by the
    /// weight of the field it reads.
    /// </summary>
    /// <remarks>
    /// The <c>simple</c> query parser takes a weight map directly, but the <c>full</c> Lucene
    /// syntax parser has no equivalent — it parses against a single default field and returns a
    /// finished query. Walking that query and boosting each clause by its field's weight gets
    /// the same effect, so a profile's weights are not silently dropped for half the query
    /// types.
    ///
    /// A clause spanning no single field, such as a nested boolean, is left alone and its own
    /// clauses are boosted individually instead. Weights multiply any boost the caller wrote
    /// into the query themselves, so <c>title:foo^2</c> under a weight of 3 ends up at 6 —
    /// the two are independent requests to weigh that clause more heavily.
    /// </remarks>
    public static Query ApplyWeights(
        SearchIndex index,
        ScoringProfile? profile,
        Query query,
        IEnumerable<string> searchFields)
    {
        var weights = GetWeights(index, profile, searchFields);

        // Nothing to do when every field is at its default weight.
        if (weights.Values.All(i => Math.Abs(i - 1.0f) < float.Epsilon))
        {
            return query;
        }

        ApplyWeights(query, weights);

        return query;
    }

    private static void ApplyWeights(Query query, IDictionary<string, float> weights)
    {
        switch (query)
        {
            case BooleanQuery boolean:
                foreach (var clause in boolean.Clauses)
                {
                    ApplyWeights(clause.Query, weights);
                }

                break;

            case TermQuery term when weights.TryGetValue(term.Term.Field, out var termWeight):
                term.Boost *= termWeight;
                break;

            case PhraseQuery phrase:
            {
                var field = phrase.GetTerms().FirstOrDefault()?.Field;

                if (field != null && weights.TryGetValue(field, out var phraseWeight))
                {
                    phrase.Boost *= phraseWeight;
                }

                break;
            }

            case MultiTermQuery multiTerm when weights.TryGetValue(multiTerm.Field, out var multiWeight):
                multiTerm.Boost *= multiWeight;
                break;
        }
    }
}
