using System.Net;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;
using SearchIndex = Azure.Search.Documents.Indexes.Models.SearchIndex;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for the search parameters that describe how a query is spread across
/// replicas, which a single local index answers exactly rather than approximately (issue #39).
/// </summary>
/// <remarks>
/// These run against the real HTTP surface because the parameters are read off the wire, and
/// the GET cases matter especially: Azure's GET syntax spells some of them differently from the
/// POST body, so a binding that missed one would let it through unnoticed.
///
/// This file previously also covered <c>scoringProfile</c> and <c>scoringParameters</c>, which
/// were refused with a 501 until scoring profiles were implemented in issue #47. They are now
/// answered, and are covered by <see cref="ScoringProfileIntegrationTests"/>.
/// </remarks>
public class ReplicaSearchParameterIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>
    /// A sticky session cannot change the response when there is one replica, so it is
    /// answered rather than refused.
    /// </summary>
    [Fact]
    public async Task SessionId_IsAnswered()
    {
        const string indexName = "test-replica-session-id";
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
        const string indexName = "test-replica-minimum-coverage";
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
        const string indexName = "test-replica-coverage-absent";
        var (indexClient, searchClient) = await SetUpAsync(indexName);

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", new SearchOptions(), TestContext.Current.CancellationToken);

        Assert.Null(response.Value.Coverage);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The parameters that are actually implemented must keep working.
    /// </summary>
    [Fact]
    public async Task SupportedParameters_AreStillAnswered()
    {
        const string indexName = "test-replica-supported-still-work";
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

    /// <summary>
    /// A GET carrying only supported parameters must still be answered, so the query-string
    /// bindings do not themselves break the working path.
    /// </summary>
    [Fact]
    public async Task GetSearch_SupportedParametersOnly_IsAnswered()
    {
        const string indexName = "test-replica-get-supported";
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
        const string indexName = "test-replica-get-params";
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
