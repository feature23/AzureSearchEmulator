using System.Net;
using Azure;
using Azure.Core.GeoJson;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for scoring profiles (issue #47), run against a containerized emulator
/// through the real Azure Search SDK.
/// </summary>
/// <remarks>
/// The SDK is the point of these tests. It models <c>ScoringProfile</c>, the four function
/// types, <c>TextWeights</c> and both enums as strongly-typed classes, so a definition the
/// emulator stored under a different property name, discriminator or enum spelling would fail
/// to deserialize here rather than pass unnoticed — which is what "full API parity" has to mean
/// in practice.
///
/// The ranking assertions check the order documents come back in rather than their scores.
/// Relative ordering is what a scoring profile exists to control and what carries over to
/// Azure; absolute scores do not, since the underlying relevance implementations differ.
/// </remarks>
public class ScoringProfileIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>
    /// Every property of every function type survives a create-then-read cycle through the SDK.
    /// </summary>
    [Fact]
    public async Task FullProfile_RoundTripsThroughTheSdk()
    {
        const string indexName = "test-scoring-round-trip";
        var indexClient = factory.CreateSearchIndexClient();

        await CreateIndexAsync(indexClient, indexName, CreateFullProfile(), defaultProfile: "boost");

        var stored = await indexClient.GetIndexAsync(indexName, TestContext.Current.CancellationToken);

        Assert.Equal("boost", stored.Value.DefaultScoringProfile);

        var profile = Assert.Single(stored.Value.ScoringProfiles);
        Assert.Equal("boost", profile.Name);
        Assert.Equal(ScoringFunctionAggregation.Maximum, profile.FunctionAggregation);

        // Field names are case-sensitive, so a weight key rewritten on the way through would
        // no longer name a real field.
        Assert.Equal(3.5, profile.TextWeights?.Weights["Name"]);
        Assert.Equal(1.0, profile.TextWeights?.Weights["Description"]);

        var magnitude = Assert.IsType<MagnitudeScoringFunction>(profile.Functions[0]);
        Assert.Equal("Rating", magnitude.FieldName);
        Assert.Equal(2.5, magnitude.Boost);
        Assert.Equal(ScoringFunctionInterpolation.Quadratic, magnitude.Interpolation);
        Assert.Equal(0, magnitude.Parameters.BoostingRangeStart);
        Assert.Equal(5, magnitude.Parameters.BoostingRangeEnd);
        Assert.True(magnitude.Parameters.ShouldBoostBeyondRangeByConstant);

        var freshness = Assert.IsType<FreshnessScoringFunction>(profile.Functions[1]);
        Assert.Equal(TimeSpan.FromDays(365), freshness.Parameters.BoostingDuration);

        var distance = Assert.IsType<DistanceScoringFunction>(profile.Functions[2]);
        Assert.Equal("here", distance.Parameters.ReferencePointParameter);
        Assert.Equal(100, distance.Parameters.BoostingDistance);

        var tag = Assert.IsType<TagScoringFunction>(profile.Functions[3]);
        Assert.Equal("mytags", tag.Parameters.TagsParameter);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Magnitude_RanksHigherRatingsFirst()
    {
        const string indexName = "test-scoring-magnitude";

        var profile = new ScoringProfile("boost")
        {
            Functions =
            {
                // Reversed so the top of the rating scale is the strong end.
                new MagnitudeScoringFunction("Rating", 10, new MagnitudeScoringParameters(5, 0)),
            },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        var ids = await SearchIdsAsync(searchClient, "widget", new SearchOptions { ScoringProfile = "boost" });

        Assert.Equal(["3", "2", "1"], ids.Take(3));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Freshness_RanksRecentDocumentsFirst()
    {
        const string indexName = "test-scoring-freshness";

        var profile = new ScoringProfile("boost")
        {
            Functions =
            {
                new FreshnessScoringFunction(
                    "Updated", 10, new FreshnessScoringParameters(TimeSpan.FromDays(365))),
            },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        var ids = await SearchIdsAsync(searchClient, "widget", new SearchOptions { ScoringProfile = "boost" });

        Assert.Equal(["3", "2", "1"], ids.Take(3));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The reference point arrives as a raw <c>name-value</c> string, whose value begins with a
    /// minus sign for a western longitude — the doubled dash that trips a parser splitting on
    /// the wrong separator.
    /// </summary>
    [Fact]
    public async Task Distance_RanksNearerDocumentsFirst()
    {
        const string indexName = "test-scoring-distance";

        var profile = new ScoringProfile("boost")
        {
            Functions =
            {
                new DistanceScoringFunction("Location", 10, new DistanceScoringParameters("here", 100)),
            },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        var options = new SearchOptions { ScoringProfile = "boost" };
        options.ScoringParameters.Add("here--122.33,47.60");

        var ids = await SearchIdsAsync(searchClient, "widget", options);

        // Seattle: document 1 sits on it, 2 is nearby, 3 is a continent away.
        Assert.Equal("1", ids[0]);
        Assert.Equal("2", ids[1]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tag_RanksDocumentsMatchingMoreTagsFirst()
    {
        const string indexName = "test-scoring-tag";

        var profile = new ScoringProfile("boost")
        {
            Functions = { new TagScoringFunction("Tags", 10, new TagScoringParameters("mytags")) },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        var options = new SearchOptions { ScoringProfile = "boost" };
        options.ScoringParameters.Add("mytags-budget,popular");

        var ids = await SearchIdsAsync(searchClient, "widget", options);

        // Document 2 carries both tags; 4 carries none.
        Assert.Equal("2", ids[0]);
        Assert.Equal("4", ids[^1]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TextWeights_FavorTheWeightedField()
    {
        const string indexName = "test-scoring-weights";

        var profile = new ScoringProfile("boost")
        {
            TextWeights = new TextWeights(new Dictionary<string, double> { ["Name"] = 20 }),
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        // "Plus" appears in one document's Name and nowhere else.
        var ids = await SearchIdsAsync(
            searchClient, "plus widget", new SearchOptions { ScoringProfile = "boost" });

        Assert.Equal("2", ids[0]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DefaultScoringProfile_AppliesWithoutBeingNamed()
    {
        const string indexName = "test-scoring-default";

        var profile = new ScoringProfile("boost")
        {
            Functions =
            {
                new MagnitudeScoringFunction("Rating", 10, new MagnitudeScoringParameters(5, 0)),
            },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile, defaultProfile: "boost");

        var ids = await SearchIdsAsync(searchClient, "widget", new SearchOptions());

        Assert.Equal(["3", "2", "1"], ids.Take(3));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The GET form of search binds its parameters off the query string and names the scoring
    /// parameter in the singular, so it has its own chance to drop one.
    /// </summary>
    [Fact]
    public async Task GetSearch_AppliesTheProfile()
    {
        const string indexName = "test-scoring-get";

        var profile = new ScoringProfile("boost")
        {
            Functions = { new TagScoringFunction("Tags", 10, new TagScoringParameters("mytags")) },
        };

        var (indexClient, _) = await SetUpAsync(indexName, profile);

        using var httpClient = factory.CreateHttpClient();

        var response = await httpClient.GetAsync(
            $"/indexes/{indexName}/docs?search=widget&scoringProfile=boost&scoringParameter=mytags-budget,popular",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var ids = System.Text.Json.Nodes.JsonNode.Parse(body)!["value"]!.AsArray()
            .Select(i => i!["Id"]!.GetValue<string>())
            .ToList();

        Assert.Equal("2", ids[0]);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnknownScoringProfile_IsRefused()
    {
        const string indexName = "test-scoring-unknown-profile";
        var (indexClient, searchClient) = await SetUpAsync(indexName, new ScoringProfile("boost"));

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            searchClient.SearchAsync<ScoredProduct>(
                "widget",
                new SearchOptions { ScoringProfile = "nope" },
                TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("nope", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A profile whose function needs a scoring parameter is refused when the request omits it,
    /// rather than answered unboosted — the same fail-loudly principle as issue #39.
    /// </summary>
    [Fact]
    public async Task MissingScoringParameter_IsRefused()
    {
        const string indexName = "test-scoring-missing-parameter";

        var profile = new ScoringProfile("boost")
        {
            Functions = { new TagScoringFunction("Tags", 10, new TagScoringParameters("mytags")) },
        };

        var (indexClient, searchClient) = await SetUpAsync(indexName, profile);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            searchClient.SearchAsync<ScoredProduct>(
                "widget",
                new SearchOptions { ScoringProfile = "boost" },
                TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("mytags", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A profile naming a field that cannot support its function is a definition error, and
    /// Azure reports it when the index is created rather than when a query uses it.
    /// </summary>
    [Fact]
    public async Task ProfileOverIncompatibleField_IsRefusedAtIndexTime()
    {
        const string indexName = "test-scoring-bad-field-type";
        var indexClient = factory.CreateSearchIndexClient();

        // A freshness function cannot read a numeric field.
        var profile = new ScoringProfile("boost")
        {
            Functions =
            {
                new FreshnessScoringFunction(
                    "Rating", 2, new FreshnessScoringParameters(TimeSpan.FromDays(1))),
            },
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => CreateIndexAsync(indexClient, indexName, profile));

        Assert.Equal((int)HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("Rating", ex.Message);
    }

    private static ScoringProfile CreateFullProfile() =>
        new("boost")
        {
            TextWeights = new TextWeights(new Dictionary<string, double>
            {
                ["Name"] = 3.5,
                ["Description"] = 1.0,
            }),
            FunctionAggregation = ScoringFunctionAggregation.Maximum,
            Functions =
            {
                new MagnitudeScoringFunction("Rating", 2.5, new MagnitudeScoringParameters(0, 5)
                {
                    ShouldBoostBeyondRangeByConstant = true,
                })
                {
                    Interpolation = ScoringFunctionInterpolation.Quadratic,
                },
                new FreshnessScoringFunction(
                    "Updated", 2, new FreshnessScoringParameters(TimeSpan.FromDays(365)))
                {
                    Interpolation = ScoringFunctionInterpolation.Logarithmic,
                },
                new DistanceScoringFunction("Location", 3, new DistanceScoringParameters("here", 100)),
                new TagScoringFunction("Tags", 4, new TagScoringParameters("mytags"))
                {
                    Interpolation = ScoringFunctionInterpolation.Constant,
                },
            },
        };

    private async Task<(SearchIndexClient IndexClient, SearchClient SearchClient)> SetUpAsync(
        string indexName,
        ScoringProfile profile,
        string? defaultProfile = null)
    {
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateIndexAsync(indexClient, indexName, profile, defaultProfile);
        await UploadDocumentsAsync(searchClient);

        return (indexClient, searchClient);
    }

    private static async Task CreateIndexAsync(
        SearchIndexClient indexClient,
        string indexName,
        ScoringProfile profile,
        string? defaultProfile = null)
    {
        try
        {
            await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // expected
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("Name") { IsFilterable = true },
                new SearchableField("Description") { IsFilterable = true },
                new SimpleField("Rating", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
                new SimpleField("Updated", SearchFieldDataType.DateTimeOffset) { IsFilterable = true },
                new SimpleField("Location", SearchFieldDataType.GeographyPoint) { IsFilterable = true },
                new SearchableField("Tags", collection: true) { IsFilterable = true },
            ],
            DefaultScoringProfile = defaultProfile,
        };

        index.ScoringProfiles.Add(profile);

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Documents that all match "widget", so the order they come back in is decided entirely by
    /// the profile under test.
    /// </summary>
    private static async Task UploadDocumentsAsync(SearchClient searchClient)
    {
        var now = DateTimeOffset.UtcNow;

        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new ScoredProduct
            {
                Id = "1", Name = "Widget Basic", Description = "a widget for testing",
                Rating = 1, Updated = now.AddDays(-300),
                Location = new GeoPoint(-122.33, 47.60), Tags = ["budget"],
            },
            new ScoredProduct
            {
                Id = "2", Name = "Widget Plus", Description = "a widget for testing",
                Rating = 3, Updated = now.AddDays(-100),
                Location = new GeoPoint(-122.20, 47.61), Tags = ["budget", "popular"],
            },
            new ScoredProduct
            {
                Id = "3", Name = "Widget Pro", Description = "a widget for testing",
                Rating = 5, Updated = now.AddDays(-1),
                Location = new GeoPoint(-74.00, 40.71), Tags = ["premium", "popular"],
            },
            // Every scored field left null, so the rule that a function does not apply to a
            // document without a value has something to act on.
            new ScoredProduct
            {
                Id = "4", Name = "Widget Plain", Description = "a widget for testing",
            },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> SearchIdsAsync(
        SearchClient searchClient,
        string search,
        SearchOptions options)
    {
        options.Size = 50;

        var response = await searchClient.SearchAsync<ScoredProduct>(
            search, options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        return results.Select(i => i.Document.Id).ToList();
    }
}
