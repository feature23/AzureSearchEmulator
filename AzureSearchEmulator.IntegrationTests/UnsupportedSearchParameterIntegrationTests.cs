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

    [Fact]
    public async Task SessionId_IsRefused()
    {
        const string indexName = "test-unsupported-session-id";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { SessionId = "session-1" };

        var ex = await AssertRefusedAsync(searchClient, options);
        Assert.Contains("sessionId", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MinimumCoverage_BelowFull_IsRefused()
    {
        const string indexName = "test-unsupported-minimum-coverage";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { MinimumCoverage = 75 };

        var ex = await AssertRefusedAsync(searchClient, options);
        Assert.Contains("minimumCoverage", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A single local index is always fully covered, so the Azure default of 100 is genuinely
    /// met rather than ignored, and the search must still run.
    /// </summary>
    [Fact]
    public async Task MinimumCoverage_OfFull_IsAnswered()
    {
        const string indexName = "test-unsupported-coverage-full";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var options = new SearchOptions { MinimumCoverage = 100 };

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", options, TestContext.Current.CancellationToken);

        var results = await response.Value.GetResultsAsync().ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, results.Count);

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
            SessionId = "session-1",
            MinimumCoverage = 50,
        };
        options.ScoringParameters.Add("mylocation--122.2,44.8");

        var ex = await AssertRefusedAsync(searchClient, options);

        Assert.Contains("scoringProfile", ex.Message);
        Assert.Contains("scoringParameters", ex.Message);
        Assert.Contains("minimumCoverage", ex.Message);
        Assert.Contains("sessionId", ex.Message);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The GET form of search, which binds its parameters off the query string rather than a
    /// JSON body and so has its own chance to drop one.
    /// </summary>
    [Theory]
    [InlineData("scoringProfile=boostByRating", "scoringProfile")]
    [InlineData("scoringParameter=mylocation--122.2,44.8", "scoringParameters")]
    [InlineData("sessionId=session-1", "sessionId")]
    [InlineData("minimumCoverage=75", "minimumCoverage")]
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
    /// The refusal is reported as itself even when the index does not exist, so a caller is
    /// not sent chasing a 404 for an index that was never the problem.
    /// </summary>
    [Fact]
    public async Task UnsupportedParameter_OnMissingIndex_ReportsTheParameter()
    {
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
