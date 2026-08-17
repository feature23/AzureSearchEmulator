using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// End-to-end tests for scoring profiles against a real index, covering what each function does
/// to the order of a result set (issue #47).
/// </summary>
/// <remarks>
/// These assert on the order documents come back in, not on the scores themselves. Relative
/// ordering is what a scoring profile exists to control and what carries over from the emulator
/// to Azure; the absolute scores do not, because the underlying relevance implementations
/// differ. See <see cref="ScoringFunctionEvaluator"/>.
///
/// Every document matches the query term, so any difference in order is the profile's doing
/// rather than the text match's.
/// </remarks>
public class ScoringProfileSearchTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public ScoringProfileSearchTests()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        _helper = new LuceneTestHelper(index, LuceneTestHelper.CreateScoringDocuments(DateTimeOffset.UtcNow));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    [Fact]
    public async Task Magnitude_RanksHigherRatingsFirst()
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 10,
            // Reversed so the top of the rating scale is the strong end.
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 5, BoostingRangeEnd = 0 },
        });

        var ids = await SearchIdsAsync(index, "widget", "boost");

        // 3 has the highest rating, then 2, then 1; 4 has none and is boosted by nothing.
        Assert.Equal(["3", "2", "1"], ids.Take(3));
    }

    [Fact]
    public async Task Freshness_RanksRecentDocumentsFirst()
    {
        var index = WithProfile(new FreshnessScoringFunction
        {
            FieldName = "Updated",
            Boost = 10,
            Freshness = new FreshnessScoringParameters { BoostingDuration = "P365D" },
        });

        var ids = await SearchIdsAsync(index, "widget", "boost");

        Assert.Equal(["3", "2", "1"], ids.Take(3));
    }

    [Fact]
    public async Task Distance_RanksNearerDocumentsFirst()
    {
        var index = WithProfile(new DistanceScoringFunction
        {
            FieldName = "Location",
            Boost = 10,
            Distance = new DistanceScoringParameters
            {
                ReferencePointParameter = "here",
                BoostingDistance = 100,
            },
        });

        // Seattle: document 1 sits on it, 2 is a few kilometers away, 3 is a continent away.
        var ids = await SearchIdsAsync(index, "widget", "boost", ["here--122.33,47.60"]);

        Assert.Equal("1", ids[0]);
        Assert.Equal("2", ids[1]);
    }

    [Fact]
    public async Task Tag_RanksDocumentsMatchingMoreTagsFirst()
    {
        var index = WithProfile(new TagScoringFunction
        {
            FieldName = "Tags",
            Boost = 10,
            Tag = new TagScoringParameters { TagsParameter = "mytags" },
        });

        // Document 2 carries both tags, 1 and 3 carry one each, 4 carries none.
        var ids = await SearchIdsAsync(index, "widget", "boost", ["mytags-budget,popular"]);

        Assert.Equal("2", ids[0]);
        Assert.Equal("4", ids[^1]);
    }

    /// <summary>
    /// A weight raises a field's contribution to the text match, so a term in the weighted
    /// field outranks the same term elsewhere.
    /// </summary>
    [Fact]
    public async Task TextWeights_FavorTheWeightedField()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        index.ScoringProfiles.Add(new ScoringProfile
        {
            Name = "boostName",
            Text = new TextWeights { Weights = { ["Name"] = 20 } },
        });

        // "Plus" appears in one document's Name and nowhere else, so with Name weighted it has
        // to come first.
        var ids = await SearchIdsAsync(index, "plus widget", "boostName");

        Assert.Equal("2", ids[0]);
    }

    /// <summary>
    /// A field weight names a field, and Lucene field names are case-sensitive, so a profile
    /// written in different casing from the schema still has to apply.
    /// </summary>
    [Fact]
    public async Task TextWeights_ResolveFieldNamesCaseInsensitively()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        index.ScoringProfiles.Add(new ScoringProfile
        {
            Name = "boostName",
            Text = new TextWeights { Weights = { ["name"] = 20 } },
        });

        var ids = await SearchIdsAsync(index, "plus widget", "boostName");

        Assert.Equal("2", ids[0]);
    }

    /// <summary>
    /// The full Lucene syntax parser takes no weight map, so the weights are applied to the
    /// parsed query instead. They must not be silently dropped for half the query types.
    /// </summary>
    [Fact]
    public async Task TextWeights_ApplyToFullQueryTypeToo()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        index.ScoringProfiles.Add(new ScoringProfile
        {
            Name = "boostName",
            Text = new TextWeights { Weights = { ["Name"] = 50 } },
        });

        var response = await _searcher.Search(index, new SearchRequest
        {
            // "plus" is in one document's Name; "widget" is in every document's Description.
            Search = "Name:plus OR Description:widget",
            QueryType = "full",
            ScoringProfile = "boostName",
            Top = 50,
        });

        var ids = response.Results.Select(i => i!["Id"]!.GetValue<string>()).ToList();

        Assert.Equal("2", ids[0]);
    }

    /// <summary>
    /// Scoring functions wrap the finished query, so they apply whichever parser produced it.
    /// </summary>
    [Fact]
    public async Task Functions_ApplyToFullQueryTypeToo()
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 10,
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 5, BoostingRangeEnd = 0 },
        });

        var response = await _searcher.Search(index, new SearchRequest
        {
            Search = "Description:widget",
            QueryType = "full",
            ScoringProfile = "boost",
            Top = 50,
        });

        var ids = response.Results.Select(i => i!["Id"]!.GetValue<string>()).ToList();

        Assert.Equal(["3", "2", "1"], ids.Take(3));
    }

    /// <summary>
    /// The profile named by the index applies when the request names none.
    /// </summary>
    [Fact]
    public async Task DefaultScoringProfile_AppliesWithoutBeingNamed()
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 10,
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 5, BoostingRangeEnd = 0 },
        });

        index.DefaultScoringProfile = "boost";

        var ids = await SearchIdsAsync(index, "widget", scoringProfile: null);

        Assert.Equal(["3", "2", "1"], ids.Take(3));
    }

    /// <summary>
    /// A request naming a profile overrides the index's default rather than combining with it.
    /// </summary>
    [Fact]
    public async Task RequestProfile_OverridesTheDefault()
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 10,
            // Boosts the LOW end, the opposite of the profile the request will name.
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 0, BoostingRangeEnd = 5 },
        });

        index.DefaultScoringProfile = "boost";

        index.ScoringProfiles.Add(new ScoringProfile
        {
            Name = "boostHigh",
            Functions =
            [
                new MagnitudeScoringFunction
                {
                    FieldName = "Rating",
                    Boost = 10,
                    Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 5, BoostingRangeEnd = 0 },
                }
            ],
        });

        var ids = await SearchIdsAsync(index, "widget", "boostHigh");

        Assert.Equal("3", ids[0]);
    }

    [Fact]
    public async Task UnknownScoringProfile_IsRefused()
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Search(index, new SearchRequest { Search = "widget", ScoringProfile = "nope" }));

        Assert.Contains("nope", ex.Message);
    }

    /// <summary>
    /// A profile whose function needs a scoring parameter cannot run without it, and Azure
    /// refuses such a query rather than answering it unboosted.
    /// </summary>
    [Fact]
    public async Task MissingScoringParameter_IsRefused()
    {
        var index = WithProfile(new TagScoringFunction
        {
            FieldName = "Tags",
            Boost = 10,
            Tag = new TagScoringParameters { TagsParameter = "mytags" },
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Search(index, new SearchRequest { Search = "widget", ScoringProfile = "boost" }));

        Assert.Contains("mytags", ex.Message);
    }

    /// <summary>
    /// Azure scores full-text search only; a wildcard search returns a uniform score, and a
    /// scoring profile has nothing to act on there.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData(null)]
    public async Task WildcardSearch_IsNotScoredByTheProfile(string? search)
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 100,
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 5, BoostingRangeEnd = 0 },
        });

        var response = await _searcher.Search(
            index,
            new SearchRequest { Search = search, ScoringProfile = "boost", Top = 50 });

        var scores = response.Results
            .Select(i => i!["@search.score"]!.GetValue<float>())
            .Distinct()
            .ToList();

        Assert.Equal(4, response.Results.Count);
        Assert.Single(scores);
    }

    /// <summary>
    /// A document with no value for a function's field is left alone rather than being boosted
    /// as though the value were zero.
    /// </summary>
    [Fact]
    public async Task DocumentWithoutTheField_IsNotBoosted()
    {
        var index = WithProfile(new MagnitudeScoringFunction
        {
            FieldName = "Rating",
            Boost = 10,
            // The low end of the range is the strong end here, so a document read as zero
            // would be boosted hardest of all, and would come first.
            Magnitude = new MagnitudeScoringParameters { BoostingRangeStart = 0, BoostingRangeEnd = 5 },
        });

        var ids = await SearchIdsAsync(index, "widget", "boost");

        Assert.NotEqual("4", ids[0]);
    }

    /// <summary>
    /// Builds the scoring index with a single-function profile named "boost".
    /// </summary>
    private static SearchIndex WithProfile(ScoringFunction function)
    {
        var index = LuceneTestHelper.CreateScoringIndex();

        index.ScoringProfiles.Add(new ScoringProfile
        {
            Name = "boost",
            Functions = [function],
        });

        return index;
    }

    private async Task<List<string>> SearchIdsAsync(
        SearchIndex index,
        string search,
        string? scoringProfile,
        IList<string>? scoringParameters = null)
    {
        var response = await _searcher.Search(index, new SearchRequest
        {
            Search = search,
            ScoringProfile = scoringProfile,
            ScoringParameters = scoringParameters,
            Top = 50,
        });

        return response.Results.Select(i => i!["Id"]!.GetValue<string>()).ToList();
    }

    private sealed class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
