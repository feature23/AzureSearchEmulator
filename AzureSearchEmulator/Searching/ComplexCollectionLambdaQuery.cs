using System.Text.Json.Nodes;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace AzureSearchEmulator.Searching;

/// <summary>
/// Matches documents whose <c>Collection(Edm.ComplexType)</c> satisfies an <c>any</c>/<c>all</c>
/// lambda, evaluating the lambda body against one element at a time.
/// </summary>
/// <remarks>
/// Evaluated in two stages, like <see cref="GeoDistanceQuery"/>. A cheap Lucene query over the
/// flattened leaf fields narrows the candidates, and the exact per-element predicate is then
/// applied to each one. For <c>any</c> the flattened query is a superset of the correct answer
/// — it ignores which element each value came from, so it can only over-match — which is what
/// makes it safe as a prefilter. For <c>all</c> there is no such prefilter, since a document
/// with no elements satisfies the lambda vacuously and would be excluded by any term query.
/// </remarks>
public class ComplexCollectionLambdaQuery : Query
{
    private readonly string _path;
    private readonly bool _isAll;
    private readonly Func<JsonObject, bool> _predicate;
    private readonly Query? _candidatePrefilter;
    private readonly string _description;

    public ComplexCollectionLambdaQuery(
        string path,
        bool isAll,
        Func<JsonObject, bool> predicate,
        Query? candidatePrefilter,
        string description)
    {
        _path = path;
        _isAll = isAll;
        _predicate = predicate;
        _candidatePrefilter = candidatePrefilter;
        _description = description;
    }

    public override Query Rewrite(IndexReader reader)
    {
        Query candidates = _isAll || _candidatePrefilter is null
            ? new MatchAllDocsQuery()
            : _candidatePrefilter;

        var exact = new FilteredQuery(
            new ConstantScoreQuery(candidates),
            new ComplexCollectionLambdaFilter(_path, _isAll, _predicate))
        {
            Boost = Boost
        };

        return exact;
    }

    public override string ToString(string field) => _description;

    // The predicate is a compiled delegate with no meaningful identity, so equality falls
    // back to the expression it was compiled from, which is what distinguishes two queries.
    public override bool Equals(object? obj) =>
        obj is ComplexCollectionLambdaQuery other
        && _path == other._path
        && _isAll == other._isAll
        && _description == other._description;

    public override int GetHashCode() => HashCode.Combine(_path, _isAll, _description);
}
