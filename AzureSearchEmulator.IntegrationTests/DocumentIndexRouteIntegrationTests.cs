using System.Net;
using System.Text;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Azure accepts a document batch on both <c>docs/search.index</c> and <c>docs/index</c>; the
/// Go SDK posts to the latter. Both spellings, in both the path and OData-key forms, must reach
/// the same handler.
/// </summary>
public class DocumentIndexRouteIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Theory]
    [InlineData("indexes/{0}/docs/index")]
    [InlineData("indexes('{0}')/docs/index")]
    [InlineData("indexes/{0}/docs/search.index")]
    public async Task IndexDocuments_AcceptsBatchOnEitherRoute(string routeFormat)
    {
        var indexName = "docs-index-route-" + Guid.NewGuid().ToString("N")[..8];
        var cancellationToken = TestContext.Current.CancellationToken;

        var indexClient = factory.CreateSearchIndexClient();
        await indexClient.CreateIndexAsync(
            new SearchIndex(indexName)
            {
                Fields =
                {
                    new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                    new SearchField("name", SearchFieldDataType.String) { IsSearchable = true },
                }
            },
            cancellationToken);

        using var http = factory.CreateHttpClient();

        var response = await http.PostAsync(
            string.Format(routeFormat, indexName) + "?api-version=2024-07-01",
            new StringContent(
                """{ "value": [ { "@search.action": "upload", "id": "1", "name": "hello" } ] }""",
                Encoding.UTF8,
                "application/json"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var searchClient = factory.CreateSearchClient(indexName);
        var document = await searchClient.GetDocumentAsync<Dictionary<string, object>>("1", cancellationToken: cancellationToken);
        Assert.Equal("hello", document.Value["name"].ToString());

        await indexClient.DeleteIndexAsync(indexName, cancellationToken);
    }
}
