namespace AzureSearchEmulator.Semantic;

/// <summary>
/// Scores how well passages answer a query, for semantic reranking, captions and answers.
/// </summary>
public interface ISemanticScorer
{
    /// <summary>
    /// Returns one relevance score per passage, each between 0 and 1.
    /// </summary>
    IReadOnlyList<double> Score(string query, IReadOnlyList<string> passages);
}
