using System.Net;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for the dashboard and health endpoint (issue #90), run against a
/// containerized emulator.
/// </summary>
/// <remarks>
/// What these are really guarding is the coexistence. Serving a UI at <c>/</c> meant taking the
/// root away from OData's service document and adding anti-forgery middleware in front of every
/// route, and both of those are the kind of change that works on the developer's machine and
/// breaks the API surface somewhere else. So the assertions come in pairs: the dashboard and
/// <c>/health</c> answer, and the endpoints the Azure SDK depends on still answer as they did.
/// </remarks>
public class DashboardIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task Root_ServesTheDashboard()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Prerendered server-side, so the status is in the markup rather than arriving later over
        // the circuit — which is also what makes it assertable without driving a browser.
        Assert.Contains("Azure Search Emulator", html);
        Assert.Contains("Health checks", html);
        Assert.Contains("Running normally", html);
    }

    /// <remarks>
    /// The search page renders only once its circuit connects (issue #92), so the static response
    /// carries the layout rather than results; what this guards is the route existing alongside the
    /// API's own routes and the navigation reaching it.
    /// </remarks>
    [Fact]
    public async Task Search_ServesThePage()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/search", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("href=\"search\" class=\"topnav-link active\"", html);
    }

    [Fact]
    public async Task Dashboard_StylesheetIsServed()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/css/dashboard.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    /// <remarks>
    /// Without Blazor's script the pages still prerender, so the dashboard looks fine, but nothing
    /// interactive ever starts and the search page, which is not prerendered, stays blank. The
    /// script comes from a package the SDK can leave out of a publish while <c>dotnet run</c> keeps
    /// working, so it is checked here against the built image (issue #92).
    /// </remarks>
    [Fact]
    public async Task Dashboard_BlazorScriptIsServed()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/_framework/blazor.web.js", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <remarks>
    /// The container has a writable indexes volume and no malformed definitions, so a healthy
    /// answer here is the real thing rather than a check that cannot fail — the unit tests cover
    /// the failing directions.
    /// </remarks>
    [Fact]
    public async Task Health_ReportsHealthy()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// The dashboard took <c>/</c> from OData's service document, which sits on the same controller
    /// as <c>$metadata</c>. Only the former was meant to go.
    /// </remarks>
    [Fact]
    public async Task Metadata_IsStillServed()
    {
        await factory.WaitUntilServingAsync();
        using var client = factory.CreateHttpClient();

        var response = await client.GetAsync("/$metadata", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("SearchIndex", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <remarks>
    /// Anti-forgery middleware runs ahead of the API routes now. It is supposed to ignore requests
    /// that carry no token, but a misconfiguration would reject exactly the unauthenticated writes
    /// every SDK client makes — so this drives one through the SDK rather than raw HTTP.
    /// </remarks>
    [Fact]
    public async Task ApiWrites_AreNotBlockedByAntiforgery()
    {
        var client = factory.CreateSearchIndexClient();
        const string indexName = "test-dashboard-antiforgery";

        var index = new SearchIndex(indexName)
        {
            Fields = { new SearchField("id", SearchFieldDataType.String) { IsKey = true } }
        };

        try
        {
            await client.CreateIndexAsync(index, TestContext.Current.CancellationToken);

            var fetched = await client.GetIndexAsync(indexName, TestContext.Current.CancellationToken);
            Assert.Equal(indexName, fetched.Value.Name);
        }
        finally
        {
            await client.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        }
    }
}
