namespace AzureSearchEmulator.Searching;

/// <summary>
/// One vector query from a search request (issue #46).
/// </summary>
/// <remarks>
/// Modelled in full even though this phase answers none of them, so that a request is refused
/// for the right reason. A <c>kind</c> of <c>text</c> is refused because it needs a hosted
/// embedding model the emulator has no way to call, which will remain true after vector search
/// works; a <c>kind</c> of <c>vector</c> is refused only because the query half is not built
/// yet. Those deserve different messages, and telling them apart needs the discriminator
/// parsed.
/// </remarks>
public class VectorQuery
{
    /// <summary>
    /// The discriminator: <c>vector</c> for a caller-supplied embedding, <c>text</c> for one
    /// the service would generate.
    /// </summary>
    /// <remarks>
    /// Left as a string rather than an enum so that an unrecognized kind reaches the emulator's
    /// own error message instead of a deserializer failure, and so that kinds added to the
    /// service later are reported as unsupported rather than malformed.
    /// </remarks>
    public string? Kind { get; set; }

    /// <summary>
    /// The query embedding, for <c>kind: vector</c>.
    /// </summary>
    public IList<float>? Vector { get; set; }

    /// <summary>
    /// The text to embed, for <c>kind: text</c>.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// How many nearest neighbours to retrieve.
    /// </summary>
    /// <remarks>
    /// Distinct from <c>$top</c>: this is how many candidates the vector query contributes,
    /// while <c>$top</c> is the size of the page returned.
    /// </remarks>
    public int? KNearestNeighborsCount { get; set; }

    /// <summary>
    /// Comma-delimited vector fields to search.
    /// </summary>
    public string? Fields { get; set; }

    /// <summary>
    /// Whether to bypass the approximate index and scan every vector.
    /// </summary>
    /// <remarks>
    /// The emulator scans exhaustively either way, so this will be accepted and ignored.
    /// </remarks>
    public bool? Exhaustive { get; set; }

    /// <summary>
    /// Multiplier on candidates retrieved before reranking, when compression is in use.
    /// </summary>
    /// <remarks>
    /// Meaningless without compression, which the emulator does not implement.
    /// </remarks>
    public double? Oversampling { get; set; }

    /// <summary>
    /// Relative weight of this query's contribution to a hybrid score.
    /// </summary>
    public float? Weight { get; set; }
}
