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
    /// Why a hybrid request is refused, shared by the two places that refuse it.
    /// </summary>
    private const string HybridUnsupportedMessage =
        "Hybrid search — combining 'search' with 'vectorQueries' — is not supported yet. " +
        "Azure fuses the two result sets with Reciprocal Rank Fusion, which this build does " +
        "not implement. Send the text query and the vector query separately.";

    /// <summary>
    /// True when the request asks for vector search at all.
    /// </summary>
    public static bool HasVectorQueries(SearchRequest request)
        => request.VectorQueries is { Count: > 0 };

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

        // Checked here rather than in Combine so that it is reported before the reader is
        // opened, like the rest of these.
        if (request.Search != null)
        {
            throw new InvalidOperationException(HybridUnsupportedMessage);
        }
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
    public static IReadOnlyList<Query> BuildQueries(
        SearchIndex index,
        SearchRequest request,
        Filter? preFilter)
    {
        var queries = new List<Query>();

        foreach (var vectorQuery in request.VectorQueries ?? [])
        {
            queries.Add(BuildQuery(index, vectorQuery, preFilter));
        }

        return queries;
    }

    /// <summary>
    /// Combines the text query and the vector queries into the single query a search runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With no vector queries this is the text query untouched, which is every request that
    /// predates this feature.
    /// </para>
    /// <para>
    /// With vector queries and no text query — the pure vector search this phase implements —
    /// the vector queries are unioned, so a document near the query vector in any of them
    /// matches, and Lucene's own coordination takes the best score among them.
    /// </para>
    /// <para>
    /// A request combining <c>search</c> with <c>vectorQueries</c> is a hybrid search, which
    /// Azure answers by fusing the two rankings with Reciprocal Rank Fusion. RRF is not
    /// implemented yet, and a union would not approximate it — the two arms produce scores on
    /// unrelated scales, so whichever happened to score higher would dominate the ranking
    /// regardless of rank. Refusing is the honest answer until the fusion exists.
    /// </para>
    /// </remarks>
    public static Query? Combine(Query? textQuery, IReadOnlyList<Query> vectorQueries, SearchRequest request)
    {
        if (vectorQueries.Count == 0)
        {
            return textQuery;
        }

        if (textQuery != null)
        {
            throw new InvalidOperationException(HybridUnsupportedMessage);
        }

        if (vectorQueries.Count == 1)
        {
            return vectorQueries[0];
        }

        var combined = new BooleanQuery();

        foreach (var query in vectorQueries)
        {
            combined.Add(query, Occur.SHOULD);
        }

        return combined;
    }

    private static Query BuildQuery(SearchIndex index, VectorQuery query, Filter? preFilter)
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

        return new VectorSearchQuery(paths, [.. query.Vector], metric, k, preFilter);
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
