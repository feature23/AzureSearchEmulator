using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for Reciprocal Rank Fusion (issue #46).
/// </summary>
/// <remarks>
/// Azure documents the formula but not the two details that decide whether an implementation
/// reproduces its numbers: whether ranks start at 0 or 1, and whether the arithmetic is single
/// or double precision. Both are recovered from the scores Azure publishes, and the tests that
/// pin them assert exact values rather than approximate ones — an implementation that got
/// either wrong would still rank correctly, so only the exact value catches it.
/// </remarks>
public class ReciprocalRankFusionTests
{
    private static ReciprocalRankFusion.Arm Arm(params int[] docIds) => new(docIds);

    /// <summary>
    /// The fixture that pins the formula, the constant, 1-based ranks and single precision all
    /// at once.
    /// </summary>
    /// <remarks>
    /// <c>0.032786883413791656</c> is the score Azure's own hybrid documentation reports for a
    /// document ranked first by both arms of a two-arm query. It is exactly
    /// <c>float32(2/61)</c>. Zero-based ranks would give <c>2/60 = 0.0333…</c> and double
    /// precision would give <c>0.03278688524590164</c>, so this single value rules out both
    /// alternatives.
    /// </remarks>
    [Fact]
    public void DocumentRankedFirstByBothArms_MatchesAzuresPublishedScore()
    {
        var fused = ReciprocalRankFusion.Fuse([Arm(7), Arm(7)]);

        var (docId, score) = Assert.Single(fused);

        Assert.Equal(7, docId);
        Assert.Equal(0.032786883413791656f, score);
    }

    /// <summary>
    /// A document in one arm only contributes exactly one term, with no penalty for its absence
    /// from the others — which is what Azure's published responses show.
    /// </summary>
    [Fact]
    public void DocumentInOneArmOnly_ContributesOneTerm()
    {
        var fused = ReciprocalRankFusion.Fuse([Arm(1), Arm(2)]);

        Assert.Equal(1f / 61f, fused.Single(i => i.DocId == 1).Score);
        Assert.Equal(1f / 61f, fused.Single(i => i.DocId == 2).Score);
    }

    [Fact]
    public void Rank_IsOneBased()
    {
        var fused = ReciprocalRankFusion.Fuse([Arm(1, 2, 3)]);

        Assert.Equal(1f / 61f, fused.Single(i => i.DocId == 1).Score);
        Assert.Equal(1f / 62f, fused.Single(i => i.DocId == 2).Score);
        Assert.Equal(1f / 63f, fused.Single(i => i.DocId == 3).Score);
    }

    /// <summary>
    /// The property that makes RRF worth implementing rather than approximating: a document both
    /// arms rank moderately well beats one a single arm ranks first, because two terms outweigh
    /// one. A union of raw scores could not produce this ordering.
    /// </summary>
    [Fact]
    public void DocumentRankedWellByBothArms_BeatsOneRankedFirstBySingleArm()
    {
        // 5 is second in both arms; 1 is first in one arm and absent from the other.
        var fused = ReciprocalRankFusion.Fuse(
        [
            Arm(1, 5),
            Arm(9, 5),
        ]);

        Assert.Equal(5, fused[0].DocId);
        Assert.Equal(2f / 62f, fused[0].Score);
    }

    [Fact]
    public void Results_AreOrderedByScoreDescending()
    {
        var fused = ReciprocalRankFusion.Fuse(
        [
            Arm(1, 2, 3),
            Arm(3, 2, 1),
        ]);

        var scores = fused.Select(i => i.Score).ToArray();

        Assert.Equal(scores.OrderByDescending(i => i), scores);
    }

    /// <summary>
    /// Azure documents no tie-break and disclaims stable ordering for equal scores, so the
    /// emulator picks one and holds to it — determinism being the property a test needs and the
    /// one thing the service does not offer.
    /// </summary>
    [Fact]
    public void Ties_AreBrokenDeterministicallyByDocumentId()
    {
        // Every document is ranked first by exactly one arm, so all three tie.
        var fused = ReciprocalRankFusion.Fuse([Arm(30), Arm(10), Arm(20)]);

        Assert.Equal([10, 20, 30], fused.Select(i => i.DocId));
        Assert.All(fused, i => Assert.Equal(1f / 61f, i.Score));
    }

    [Fact]
    public void Weight_ScalesAnArmsContribution()
    {
        var fused = ReciprocalRankFusion.Fuse(
        [
            new ReciprocalRankFusion.Arm([1], Weight: 2f),
            new ReciprocalRankFusion.Arm([2], Weight: 0.5f),
        ]);

        Assert.Equal(2f / 61f, fused.Single(i => i.DocId == 1).Score);
        Assert.Equal(0.5f / 61f, fused.Single(i => i.DocId == 2).Score);
    }

    /// <summary>
    /// A weight is only worth honouring if it can actually change the ranking, which is the
    /// point of exposing it.
    /// </summary>
    [Fact]
    public void Weight_CanReorderTheResult()
    {
        // Unweighted, doc 1 would win on rank alone.
        var unweighted = ReciprocalRankFusion.Fuse([Arm(1), Arm(2)]);
        Assert.Equal(1, unweighted[0].DocId);

        var weighted = ReciprocalRankFusion.Fuse(
        [
            new ReciprocalRankFusion.Arm([1], Weight: 1f),
            new ReciprocalRankFusion.Arm([2], Weight: 3f),
        ]);

        Assert.Equal(2, weighted[0].DocId);
    }

    [Fact]
    public void DefaultWeight_IsOne()
    {
        var explicitlyOne = ReciprocalRankFusion.Fuse([new ReciprocalRankFusion.Arm([1], Weight: 1f)]);
        var defaulted = ReciprocalRankFusion.Fuse([new ReciprocalRankFusion.Arm([1])]);

        Assert.Equal(explicitlyOne[0].Score, defaulted[0].Score);
    }

    [Fact]
    public void NoArms_FusesToNothing()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([]));
    }

    [Fact]
    public void EmptyArms_FuseToNothing()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([Arm(), Arm()]));
    }

    /// <summary>
    /// More than two arms is the ordinary case for a hybrid query over several vector fields:
    /// each field is its own retrieval system, so a query naming three fields alongside a text
    /// query fuses four lists.
    /// </summary>
    [Fact]
    public void ManyArms_EachContributeATerm()
    {
        var fused = ReciprocalRankFusion.Fuse([Arm(1), Arm(1), Arm(1), Arm(1)]);

        Assert.Equal(4f / 61f, Assert.Single(fused).Score);
    }

    /// <summary>
    /// The scores RRF produces are small enough to look like non-matches, which Azure warns
    /// about directly. Worth pinning so the magnitude is a documented expectation rather than a
    /// surprise.
    /// </summary>
    [Fact]
    public void Scores_AreSmallByConstruction()
    {
        var fused = ReciprocalRankFusion.Fuse([Arm(1), Arm(1)]);

        Assert.InRange(fused[0].Score, 0f, 2f / ReciprocalRankFusion.RankConstant);
    }
}
