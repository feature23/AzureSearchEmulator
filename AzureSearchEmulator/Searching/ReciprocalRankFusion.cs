namespace AzureSearchEmulator.Searching;

/// <summary>
/// Fuses the ranked result lists of a hybrid query into one ranking, by Reciprocal Rank Fusion
/// (issue #46).
/// </summary>
/// <remarks>
/// <para>
/// A hybrid query runs more than one retrieval system — a full-text query and one or more
/// vector queries — and their scores are not comparable: a BM25 score is unbounded and a
/// cosine score sits in [0, 1], so whichever happened to be larger would decide the ranking
/// outright. RRF sidesteps that by discarding the scores and fusing on <em>rank</em>, which
/// every arm expresses on the same scale.
/// </para>
/// <para>
/// Azure documents the formula as <c>1/(rank + k)</c> summed over the arms a document appears
/// in, with <c>k</c> a constant it sets to 60 and does not expose as a parameter. Two details
/// the documentation does not state are recovered from the scores it publishes: ranks are
/// 1-based, and the arithmetic is single-precision. A document ranked first by two arms scores
/// <c>float32(2/61) = 0.032786883413791656</c>, which is exactly the value Azure's own hybrid
/// example reports for such a document — reproducing it is what
/// <c>ReciprocalRankFusionTests</c> pins.
/// </para>
/// <para>
/// The scores are small by construction: one arm contributes at most <c>1/61 ≈ 0.0164</c>, so a
/// two-arm hybrid tops out near 0.033. Azure warns about this directly — a score of 0.03 is a
/// strong match, not a weak one — and it is worth knowing before comparing a hybrid score
/// against the <c>@search.score</c> of a pure vector or pure text query, which are on entirely
/// different scales.
/// </para>
/// </remarks>
public static class ReciprocalRankFusion
{
    /// <summary>
    /// The <c>k</c> constant in <c>1/(rank + k)</c>.
    /// </summary>
    /// <remarks>
    /// Azure documents 60 and exposes no way to change it, so this is a constant here too
    /// rather than a setting. It is unrelated to a vector query's <c>k</c>, which is how many
    /// neighbours that query retrieves; the documentation calls the collision out explicitly.
    /// </remarks>
    public const int RankConstant = 60;

    /// <summary>
    /// One retrieval system's contribution to the fusion: its documents in rank order.
    /// </summary>
    /// <param name="DocIds">
    /// Lucene document ids, best first. Position in this list <em>is</em> the rank, so the
    /// caller is responsible for having ordered it by that arm's own score.
    /// </param>
    /// <param name="Weight">
    /// Multiplies this arm's contribution. Azure allows it on a vector query and documents the
    /// default as 1.0; the text arm always has an implicit weight of 1.0 and cannot be given
    /// one.
    /// </param>
    public readonly record struct Arm(IReadOnlyList<int> DocIds, float Weight = 1f);

    /// <summary>
    /// Fuses the arms into a single ranking, best first.
    /// </summary>
    /// <remarks>
    /// A document appearing in several arms accumulates a term from each; one appearing in a
    /// single arm contributes exactly one term and is not penalized for its absence elsewhere,
    /// which is the behaviour Azure's published responses show.
    /// </remarks>
    public static IReadOnlyList<(int DocId, float Score)> Fuse(IReadOnlyList<Arm> arms)
    {
        var scores = new Dictionary<int, float>();

        foreach (var arm in arms)
        {
            for (var i = 0; i < arm.DocIds.Count; i++)
            {
                // Ranks are 1-based: the first document in an arm contributes 1/(1 + 60), not
                // 1/(0 + 60). Recovered from the scores Azure publishes — see the class remarks.
                var rank = i + 1;

                var contribution = arm.Weight / (RankConstant + rank);

                scores[arm.DocIds[i]] = scores.GetValueOrDefault(arm.DocIds[i]) + contribution;
            }
        }

        return scores
            .OrderByDescending(i => i.Value)
            // Azure documents no tie-break and explicitly disclaims stable ordering for equal
            // scores, so any rule is as faithful as any other. Ordering by Lucene's internal
            // document id makes the emulator's answer reproducible for a given index, which is
            // the property a test needs and the one thing the service cannot offer. Note it is
            // reproducible rather than stable: the id is an index position, not the document
            // key, so it can change when segments merge.
            .ThenBy(i => i.Key)
            .Select(i => (i.Key, i.Value))
            .ToList();
    }
}
