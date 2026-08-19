using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Turns the <c>vectorQueries</c> of a request into Lucene queries, validating them against the
/// index first (issue #46).
/// </summary>
/// <remarks>
/// Validation is deliberately strict and happens before any scan. A vector query naming a field
/// that is not a vector field, or carrying a vector of the wrong length, is a fault in the
/// request that the emulator can name precisely — and reporting it is far more useful than
/// returning the empty result set the scan would otherwise produce, which looks identical to a
/// genuine absence of near neighbours.
/// </remarks>
public static class VectorQuerySupport
{
    /// <summary>
    /// The default <c>k</c> when a query does not name one.
    /// </summary>
    /// <remarks>
    /// Matches the default page size, so a vector query with no <c>k</c> returns a full page.
    /// </remarks>
    public const int DefaultK = 50;

    /// <summary>
    /// How many documents the text arm of a hybrid query contributes to the fusion.
    /// </summary>
    /// <remarks>
    /// Azure calls this <c>maxTextRecallSize</c> and defaults it to 1000, far above the default
    /// page size. The fusion needs enough of each arm's ranking to find the documents the arms
    /// agree on: a document the text arm ranks 200th can still finish near the top once a
    /// vector arm's opinion is added, and it could not if the text arm only offered its first
    /// 50.
    /// </remarks>
    public const int DefaultTextRecallSize = 1000;

    /// <summary>
    /// True when the request asks for vector search at all.
    /// </summary>
    public static bool HasVectorQueries(SearchRequest request)
        => request.VectorQueries is { Count: > 0 };

    /// <summary>
    /// True when the request carries a full-text query, as opposed to none or a wildcard.
    /// </summary>
    /// <remarks>
    /// <c>*</c> and <c>*:*</c> are match-all rather than searches for anything, which is the
    /// same distinction <c>LuceneNetIndexSearcher.IsScored</c> draws when deciding whether a
    /// scoring profile has something to act on. It matters here because
    /// <c>"search": "*"</c> alongside <c>vectorQueries</c> is a pure vector search, not a
    /// hybrid one — Azure treats it that way, REST samples routinely send it, and refusing it
    /// as an unsupported hybrid would reject a request with no text query in it.
    /// </remarks>
    public static bool HasTextQuery(SearchRequest request)
        => request.Search is not (null or "*" or "*:*");

    /// <summary>
    /// Checks the request's vector queries against the index, throwing on the first fault.
    /// </summary>
    /// <remarks>
    /// Runs before the index reader is opened so that a malformed request is reported as such,
    /// rather than as whatever the storage layer says about an index that may hold no documents
    /// yet. Shares its checks with <see cref="BuildQueries"/> by building the same queries and
    /// discarding them; they are cheap to construct, since the scan does not run until the
    /// query is rewritten.
    /// </remarks>
    public static void Validate(SearchIndex index, SearchRequest request)
    {
        if (!HasVectorQueries(request))
        {
            return;
        }

        BuildQueries(index, request, preFilter: null);
    }

    /// <summary>
    /// Builds one Lucene query per vector query in the request.
    /// </summary>
    /// <param name="preFilter">
    /// The request's <c>$filter</c>, applied during the scan under <c>preFilter</c> mode and
    /// left for the surrounding query to apply under <c>postFilter</c>.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The request asks for something the emulator cannot answer, or names something the index
    /// does not define. The controller turns this into a 400.
    /// </exception>
    public static IReadOnlyList<VectorSearchQuery> BuildQueries(
        SearchIndex index,
        SearchRequest request,
        Filter? preFilter)
    {
        var queries = new List<VectorSearchQuery>();

        foreach (var vectorQuery in request.VectorQueries ?? [])
        {
            queries.AddRange(BuildQuery(index, vectorQuery, preFilter));
        }

        return queries;
    }

    /// <summary>
    /// Combines the text query and the vector queries into the single query a search runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no vector queries this is the text query untouched, which is every request that
    /// predates this feature. With exactly one vector query and no text query it is that query,
    /// scored by similarity — the pure vector search of phase 2.
    /// </para>
    /// <para>
    /// Anything else is a fusion. That covers the obvious hybrid case, a text query alongside
    /// vector queries, but also a vector-only request with more than one arm: two vector fields
    /// produce two rankings, and those are no more comparable to each other than a vector
    /// ranking is to a text one when the fields are bound to different profiles. Azure fuses
    /// both cases the same way, so the emulator does too.
    /// </para>
    /// <para>
    /// Note what fusion replaces: a <see cref="BooleanQuery"/> union of the arms. Lucene sums
    /// the scores of matching <see cref="Occur.SHOULD"/> clauses and, with coordination enabled,
    /// scales each document by the fraction of clauses it matched — so a document with a perfect
    /// match on one field and no vector for another is halved to 0.5, while a mediocre match on
    /// both sums past 1.0 and outranks it. Both are wrong: a similarity is not additive, and a
    /// document is not a worse match for lacking a field the query happened to name. Fusing on
    /// rank sidesteps the arithmetic entirely.
    /// </para>
    /// </remarks>
    public static Query? Combine(
        Query? textQuery,
        IReadOnlyList<VectorSearchQuery> vectorQueries,
        SearchRequest request)
    {
        if (vectorQueries.Count == 0)
        {
            return textQuery;
        }

        // A single vector arm has nothing to fuse with, and fusing it with itself would replace
        // the similarity score with a rank-derived one for no gain — so the raw similarity is
        // kept, which is what a caller inspecting @search.score on a pure vector query expects.
        if (textQuery == null && vectorQueries.Count == 1)
        {
            return vectorQueries[0];
        }

        return new HybridSearchQuery(textQuery, vectorQueries);
    }

    private static IReadOnlyList<VectorSearchQuery> BuildQuery(
        SearchIndex index,
        VectorQuery query,
        Filter? preFilter)
    {
        // Refused rather than answered wrongly: a text query needs a hosted embedding model,
        // and there is no way to approximate one that would not silently return the wrong
        // neighbours.
        if (query.Kind != null && !string.Equals(query.Kind, "vector", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Equals(query.Kind, "text", StringComparison.OrdinalIgnoreCase)
                    ? "Vector queries of kind 'text' are not supported: generating an embedding " +
                      "from query text requires a hosted embedding model. Supply a precomputed " +
                      "embedding with kind 'vector' instead."
                    : $"Vector queries of kind '{query.Kind}' are not supported; supply a " +
                      "precomputed embedding with kind 'vector'.");
        }

        if (query.Vector is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "A vector query must supply a non-empty 'vector'.");
        }

        var paths = ResolveFields(index, query);
        var metric = ResolveMetric(index, paths);

        foreach (var path in paths)
        {
            var field = ComplexTypeSupport.FindFieldByPath(index, path)!;

            // The query vector has to match the field it is compared against; a dot product
            // between vectors of different length is not a smaller similarity, it is undefined.
            if (field.Dimensions is { } dimensions && dimensions != query.Vector.Count)
            {
                throw new InvalidOperationException(
                    $"The vector query against field '{path}' has {query.Vector.Count} " +
                    $"dimensions but the field expects {dimensions}.");
            }
        }

        var k = query.KNearestNeighborsCount ?? DefaultK;

        if (k < 1)
        {
            throw new InvalidOperationException(
                $"A vector query's k must be at least 1, but was {k}.");
        }

        var weight = query.Weight ?? 1f;

        if (weight <= 0 || !float.IsFinite(weight))
        {
            throw new InvalidOperationException(
                $"A vector query's weight must be a positive number, but was {weight}.");
        }

        var vector = query.Vector.ToArray();

        // One query per field rather than one per vector query. Azure counts each field as its
        // own query execution — its documentation works the arithmetic through explicitly, a
        // text query plus two vector queries over five fields being eleven executions — and a
        // hybrid search fuses one ranked list per execution. Building them separately is what
        // lets each contribute its own term to the fusion.
        return [.. paths.Select(path =>
            new VectorSearchQuery(path, vector, metric, k, preFilter) { Weight = weight })];
    }

    /// <summary>
    /// Resolves the fields a vector query searches, defaulting to every vector field in the
    /// index when it names none.
    /// </summary>
    private static IReadOnlyList<string> ResolveFields(SearchIndex index, VectorQuery query)
    {
        var vectorFields = ComplexTypeSupport.EnumerateLeafFields(index)
            .Where(i => i.Field.IsVectorField())
            .ToList();

        if (string.IsNullOrWhiteSpace(query.Fields))
        {
            if (vectorFields.Count == 0)
            {
                throw new InvalidOperationException(
                    "The index defines no vector fields, so it cannot be searched by vector.");
            }

            return vectorFields.Select(i => i.Path).ToList();
        }

        var named = query.Fields
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var paths = new List<string>();

        foreach (var name in named)
        {
            var match = vectorFields
                .FirstOrDefault(i => string.Equals(i.Path, name, StringComparison.OrdinalIgnoreCase));

            if (match.Field == null)
            {
                // Naming a real field of the wrong type is a different mistake from naming one
                // that does not exist, and the message says which.
                throw new InvalidOperationException(
                    ComplexTypeSupport.FindFieldByPath(index, name) == null
                        ? $"The vector query names field '{name}', which does not exist in the index."
                        : $"The vector query names field '{name}', which is not a vector field.");
            }

            paths.Add(match.Path);
        }

        return paths;
    }

    /// <summary>
    /// Resolves the metric the named fields are searched under.
    /// </summary>
    /// <remarks>
    /// Every field in one vector query must agree on a metric, because the query produces a
    /// single ranking and scores computed under different metrics are not comparable — a cosine
    /// score of 0.9 and a Euclidean one of 0.9 say different things about distance. Azure binds
    /// the metric to the field's profile, so fields bound to profiles that disagree cannot be
    /// searched together.
    /// </remarks>
    private static VectorSearchMetric ResolveMetric(SearchIndex index, IReadOnlyList<string> paths)
    {
        VectorSearchMetric? resolved = null;
        string? resolvedPath = null;

        foreach (var path in paths)
        {
            var field = ComplexTypeSupport.FindFieldByPath(index, path)!;

            var metric = field.VectorSearchProfile is { } profileName
                ? index.VectorSearch?.ResolveMetric(profileName) ?? VectorSearchMetric.Cosine
                : VectorSearchMetric.Cosine;

            if (resolved is { } existing && existing != metric)
            {
                throw new InvalidOperationException(
                    $"The vector query searches fields '{resolvedPath}' and '{path}', whose " +
                    $"profiles use different metrics ({existing} and {metric}). Fields searched " +
                    "together must share a metric.");
            }

            resolved = metric;
            resolvedPath = path;
        }

        return resolved ?? VectorSearchMetric.Cosine;
    }
}
