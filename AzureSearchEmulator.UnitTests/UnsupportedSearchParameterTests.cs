using AzureSearchEmulator.Searching;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the refusal of search parameters the emulator binds but does not act on
/// (issue #39).
/// </summary>
/// <remarks>
/// The behaviour under test is deliberately a refusal rather than an implementation: a
/// request using one of these parameters must not come back with results that quietly differ
/// from what Azure would return. The tests therefore assert both halves — that an
/// unsupported parameter is named, and that a supported or absent one is left alone, since a
/// rule that rejected too much would break working queries.
/// </remarks>
public class UnsupportedSearchParameterTests
{
    [Fact]
    public void EmptyRequest_IsAccepted()
    {
        Assert.Null(UnsupportedSearchParameters.GetRejectionMessage(new SearchRequest()));
    }

    [Fact]
    public void SupportedParameters_AreAccepted()
    {
        // Every parameter the searcher genuinely reads, including the two that were on the
        // issue's list and have since been implemented.
        var request = new SearchRequest
        {
            Count = true,
            Search = "seattle",
            Filter = "Rating gt 3",
            Orderby = "Rating desc",
            Select = "Id,Name",
            Facets = ["Category"],
            Highlight = "Name",
            HighlightPreTag = "<b>",
            HighlightPostTag = "</b>",
            QueryType = "full",
            SearchFields = "Name",
            SearchMode = "all",
            Skip = 10,
            Top = 5,
        };

        Assert.Null(UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    [Fact]
    public void ScoringProfile_IsRejected()
    {
        var request = new SearchRequest { ScoringProfile = "boostByRating" };

        Assert.Contains("scoringProfile", UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    [Fact]
    public void ScoringParameters_AreRejected()
    {
        var request = new SearchRequest { ScoringParameters = ["mylocation--122.2,44.8"] };

        Assert.Contains("scoringParameters", UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    [Fact]
    public void SessionId_IsRejected()
    {
        var request = new SearchRequest { SessionId = "session-1" };

        Assert.Contains("sessionId", UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    /// <summary>
    /// An empty scoring-parameter list is what "none given" looks like once it has been
    /// through the SDK, and asks for nothing, so it must not trip the refusal.
    /// </summary>
    [Fact]
    public void EmptyScoringParameters_AreAccepted()
    {
        var request = new SearchRequest { ScoringParameters = [] };

        Assert.Null(UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    /// <summary>
    /// 100 is Azure's default and means "the whole index must be covered", which a single
    /// local index always satisfies. Refusing it would reject a request the emulator answers
    /// correctly, so only a caller lowering the bar is refused.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(null)]
    public void MinimumCoverage_MetByASingleIndex_IsAccepted(double? minimumCoverage)
    {
        var request = new SearchRequest { MinimumCoverage = minimumCoverage };

        Assert.Null(UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(99.9)]
    public void MinimumCoverage_BelowFull_IsRejected(double minimumCoverage)
    {
        var request = new SearchRequest { MinimumCoverage = minimumCoverage };

        Assert.Contains("minimumCoverage", UnsupportedSearchParameters.GetRejectionMessage(request));
    }

    /// <summary>
    /// A caller clearing these out should learn the whole set at once rather than discovering
    /// them one failed request at a time.
    /// </summary>
    [Fact]
    public void MultipleUnsupportedParameters_AreAllReported()
    {
        var request = new SearchRequest
        {
            ScoringProfile = "boostByRating",
            ScoringParameters = ["mylocation--122.2,44.8"],
            MinimumCoverage = 75,
            SessionId = "session-1",
        };

        var message = UnsupportedSearchParameters.GetRejectionMessage(request);

        Assert.NotNull(message);
        Assert.Contains("scoringProfile", message);
        Assert.Contains("scoringParameters", message);
        Assert.Contains("minimumCoverage", message);
        Assert.Contains("sessionId", message);
    }

    /// <summary>
    /// The emulator reads <c>scoringStatistics</c> no more than it reads the rest, but unlike
    /// them it cannot change which documents match or how they rank here: 'local' and 'global'
    /// differ only in whether term statistics are aggregated across replicas, and there is
    /// one replica. It is therefore accepted rather than refused.
    /// </summary>
    [Theory]
    [InlineData("local")]
    [InlineData("global")]
    public void ScoringStatistics_IsAccepted(string scoringStatistics)
    {
        var request = new SearchRequest { ScoringStatistics = scoringStatistics };

        Assert.Null(UnsupportedSearchParameters.GetRejectionMessage(request));
    }
}
