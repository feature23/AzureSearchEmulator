using System.Net;
using System.Net.Http.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for the refusal of search parameters the emulator accepts but does not
/// act on (issue #39), run against a containerized emulator.
/// </summary>
/// <remarks>
/// These run against the real HTTP surface because that is where the bug lived: the
/// parameters were bound off the wire and then dropped, so a test that never crosses the
/// wire cannot show that they now arrive and are refused. The GET cases matter for the same
/// reason — Azure's GET syntax names <c>scoringParameter</c> in the singular, and a binding
/// that missed it would let the parameter through unnoticed, which is the original bug in a
/// new place.
/// </remarks>
public class UnsupportedSearchParameterIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task ScoringProfile_IsRefused()
    {
        const string indexName = "test-unsupported-scoring-profile";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { ScoringProfile = "boostByRating" };

        var ex = await AssertRefusedAsync(searchClient, options);
        Assert.Contains("scoringProfile", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScoringParameters_AreRefused()
    {
        const string indexName = "test-unsupported-scoring-parameters";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions();
        options.ScoringParameters.Add("mylocation--122.2,44.8");

        var ex = await AssertRefusedAsync(searchClient, options);
        Assert.Contains("scoringParameters", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A sticky session cannot change the response when there is one replica, so it is
    /// answered rather than refused.
    /// </summary>
    [Fact]
    public async Task SessionId_IsAnswered()
    {
        const string indexName = "test-unsupported-session-id";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { SessionId = "session-1" };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A single local index is always fully covered, so any floor the caller sets is genuinely
    /// met, and the coverage actually achieved is reported back.
    /// </summary>
    [Theory]
    [InlineData(100.0)]
    [InlineData(75.0)]
    public async Task MinimumCoverage_IsAnsweredAndCoverageReported(double minimumCoverage)
    {
        const string indexName = "test-unsupported-minimum-coverage";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { MinimumCoverage = minimumCoverage };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        // Read through the SDK's own Coverage property, so this fails if the emulator emits
        // the value under a name or shape the SDK cannot parse.
        Assert.Equal(100.0, response.Value.Coverage);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Azure omits <c>@search.coverage</c> unless the request asked for it, and the SDK
    /// surfaces that absence as a null. A caller's null check has to be reachable locally.
    /// </summary>
    [Fact]
    public async Task WithoutMinimumCoverage_CoverageIsNotReported()
    {
        const string indexName = "test-unsupported-coverage-absent";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", new SearchOptions(), TestContext.Current.CancellationToken);

        Assert.Null(response.Value.Coverage);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The parameters that are actually implemented must keep working; a refusal rule that
    /// caught them would be a worse regression than the silent divergence it replaced.
    /// </summary>
    [Fact]
    public async Task SupportedParameters_AreStillAnswered()
    {
        const string indexName = "test-unsupported-supported-still-work";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions
        {
            Filter = "Rating gt 3",
            Size = 10,
            IncludeTotalCount = true,
        };
        options.Select.Add("Id");
        options.Facets.Add("Category");
        options.OrderBy.Add("Rating desc");

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a"], results.Select(r => (string)r.Document["Id"]));
        Assert.NotNull(response.Value.Facets);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AllUnsupportedParameters_AreReportedTogether()
    {
        const string indexName = "test-unsupported-all";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions
        {
            ScoringProfile = "boostByRating",
            // Supported, and set here to prove they are not dragged into someone else's
            // rejection — a caller told to remove a working parameter would be misled.
            SessionId = "session-1",
            MinimumCoverage = 50,
        };
        options.ScoringParameters.Add("mylocation--122.2,44.8");

        var ex = await AssertRefusedAsync(searchClient, options);

        Assert.Contains("scoringProfile", ex.Message);
        Assert.Contains("scoringParameters", ex.Message);
        Assert.DoesNotContain("minimumCoverage", ex.Message);
        Assert.DoesNotContain("sessionId", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The GET form of search, which binds its parameters off the query string rather than a
    /// JSON body and so has its own chance to drop one.
    /// </summary>
    [Theory]
    [InlineData("scoringProfile=boostByRating", "scoringProfile")]
    [InlineData("scoringParameter=mylocation--122.2,44.8", "scoringParameters")]
    public async Task GetSearch_UnsupportedParameter_IsRefused(string queryString, string expected)
    {
        const string indexName = "test-unsupported-get";
        var (indexClient, _) = await SetUpAsync(indexName);

        using var httpClient = factory.CreateHttpClient();

        var response = await httpClient.GetAsync(
            $"/indexes/{indexName}/docs?search=*&{queryString}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A GET carrying only supported parameters must still be answered, so the query-string
    /// bindings added for the refusal do not themselves break the working path.
    /// </summary>
    [Fact]
    public async Task GetSearch_SupportedParametersOnly_IsAnswered()
    {
        const string indexName = "test-unsupported-get-supported";
        var (indexClient, _) = await SetUpAsync(indexName);

        using var httpClient = factory.CreateHttpClient();

        var response = await httpClient.GetAsync(
            $"/indexes/{indexName}/docs?search=*&$select=Id&$orderby=Rating desc",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The replica-shaped parameters are answered on the GET path too, and coverage comes
    /// back in the response body rather than being dropped along with them.
    /// </summary>
    [Fact]
    public async Task GetSearch_MinimumCoverageAndSessionId_AreAnswered()
    {
        const string indexName = "test-unsupported-get-replica-params";
        var (indexClient, _) = await SetUpAsync(indexName);

        using var httpClient = factory.CreateHttpClient();

        var response = await httpClient.GetAsync(
            $"/indexes/{indexName}/docs?search=*&minimumCoverage=75&sessionId=session-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("@search.coverage", body);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The refusal is reported as itself even when the index does not exist, so a caller is
    /// not sent chasing a 404 for an index that was never the problem.
    /// </summary>
    [Fact]
    public async Task UnsupportedParameter_OnMissingIndex_ReportsTheParameter()
    {
        // The raw HttpClient has no retry, unlike the SDK clients, so it cannot absorb the
        // connection failures that happen while the container's TLS listener is still coming
        // up — the wait strategy only checks that the port is open. Going through the SDK
        // first blocks until the emulator is genuinely serving.
        await factory.WaitUntilServingAsync();

        using var httpClient = factory.CreateHttpClient();

        var response = await httpClient.PostAsJsonAsync(
            "/indexes/test-unsupported-no-such-index/docs/search",
            new { search = "*", scoringProfile = "boostByRating" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Contains(
            "scoringProfile",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Runs a search expected to be refused, returning the failure so the caller can assert
    /// on which parameters it names.
    /// </summary>
    private static async Task<RequestFailedException> AssertRefusedAsync(
        SearchClient searchClient,
        SearchOptions options)
    {
        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            searchClient.SearchAsync<SearchDocument>("*", options, TestContext.Current.CancellationToken));

        Assert.Equal((int)HttpStatusCode.NotImplemented, ex.Status);

        return ex;
    }

    private async Task<(SearchIndexClient IndexClient, SearchClient SearchClient)> SetUpAsync(string indexName)
    {
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        return (indexClient, searchClient);
    }

    private static async Task CreateIndexAsync(SearchIndexClient indexClient, string indexName)
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
                new SimpleField("Category", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("Rating", SearchFieldDataType.Int32) { IsFilterable = true, IsSortable = true },
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);
    }

    private static async Task UploadDocumentsAsync(SearchClient searchClient)
    {
        var batch = IndexDocumentsBatch.Upload(new[]
        {
            new SearchDocument
            {
                ["Id"] = "a", ["Name"] = "Alpha", ["Category"] = "Electronics", ["Rating"] = 5,
            },
            new SearchDocument
            {
                ["Id"] = "b", ["Name"] = "Bravo", ["Category"] = "Accessories", ["Rating"] = 3,
            },
        });

        await searchClient.IndexDocumentsAsync(batch, cancellationToken: TestContext.Current.CancellationToken);
    }
}
