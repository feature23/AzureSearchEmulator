using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the <c>@search.coverage</c> value reported alongside results (issue #39).
/// </summary>
/// <remarks>
/// The value itself is uninteresting — a single local index is always fully covered — so what
/// these pin down is when the field is present at all. Azure reports coverage only for a
/// request that supplied <c>minimumCoverage</c>, and a caller's null check on
/// <c>SearchResults.Coverage</c> depends on that: emitting it unconditionally would make the
/// check unreachable locally while it still fires against the real service.
/// </remarks>
public class SearchCoverageTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public SearchCoverageTests()
    {
        _helper = new LuceneTestHelper(
            LuceneTestHelper.CreateProductIndex(),
            LuceneTestHelper.CreateProductDocuments());

        _searcher = new LuceneNetIndexSearcher(new StubIndexReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    [Fact]
    public void GetCoverage_WithoutMinimumCoverage_IsOmitted()
    {
        Assert.Null(SearchCoverage.GetCoverage(new SearchRequest()));
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(75.0)]
    [InlineData(0.0)]
    public void GetCoverage_WithMinimumCoverage_IsFull(double minimumCoverage)
    {
        var request = new SearchRequest { MinimumCoverage = minimumCoverage };

        Assert.Equal(SearchCoverage.Full, SearchCoverage.GetCoverage(request));
    }

    [Fact]
    public async Task Search_WithoutMinimumCoverage_ReportsNoCoverage()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest { Search = "*", Top = 50 });

        Assert.Null(response.Coverage);
        Assert.Equal(5, response.Results.Count);
    }

    /// <summary>
    /// A floor below 100 is met rather than refused, and the search runs normally.
    /// </summary>
    [Fact]
    public async Task Search_WithPartialMinimumCoverage_IsAnsweredInFull()
    {
        var request = new SearchRequest { Search = "*", Top = 50, MinimumCoverage = 75 };

        var response = await _searcher.Search(_helper.Index, request);

        Assert.Equal(SearchCoverage.Full, response.Coverage);
        Assert.Equal(5, response.Results.Count);
    }

    /// <summary>
    /// Coverage says how much of the index was searched, not how much of it matched, so it
    /// stays at 100 for a query that finds nothing.
    /// </summary>
    [Fact]
    public async Task Search_MatchingNothing_StillReportsFullCoverage()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category eq 'NoSuchCategory'",
            Top = 50,
            MinimumCoverage = 100,
        };

        var response = await _searcher.Search(_helper.Index, request);

        Assert.Empty(response.Results);
        Assert.Equal(SearchCoverage.Full, response.Coverage);
    }

    /// <summary>
    /// $top=0 skips the document pass entirely, which is a separate return path and so needs
    /// its own coverage check.
    /// </summary>
    [Fact]
    public async Task Search_WithNoDocumentsRequested_StillReportsCoverage()
    {
        var request = new SearchRequest { Search = "*", Top = 0, Count = true, MinimumCoverage = 100 };

        var response = await _searcher.Search(_helper.Index, request);

        Assert.Empty(response.Results);
        Assert.Equal(5, response.Count);
        Assert.Equal(SearchCoverage.Full, response.Coverage);
    }

    /// <summary>
    /// A sticky session changes nothing about the result, which is why it is accepted rather
    /// than refused.
    /// </summary>
    [Fact]
    public async Task Search_WithSessionId_IsAnsweredNormally()
    {
        var request = new SearchRequest { Search = "*", Top = 50, SessionId = "session-1" };

        var response = await _searcher.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
        Assert.Null(response.Coverage);
    }

    /// <summary>
    /// Stub implementation of ILuceneIndexReaderFactory backed by a RAMDirectory.
    /// </summary>
    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }
}
