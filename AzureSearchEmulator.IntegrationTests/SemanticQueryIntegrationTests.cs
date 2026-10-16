using System.Net;
using System.Text;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Semantic ranking depends on a hosted reranker, so the emulator refuses it with a 400 rather
/// than silently returning lexical results.
/// </summary>
public class SemanticQueryIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task SemanticQueryType_IsRejectedWithBadRequest()
    {
        var indexName = "semantic-" + Guid.NewGuid().ToString("N")[..8];
        var cancellationToken = TestContext.Current.CancellationToken;

        await factory.CreateSearchIndexClient().CreateIndexAsync(
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

        var post = await http.PostAsync(
            $"/indexes/{indexName}/docs/search?api-version=2024-07-01",
            new StringContent("""{ "search": "x", "queryType": "semantic" }""", Encoding.UTF8, "application/json"),
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("semantic", await post.Content.ReadAsStringAsync(cancellationToken));

        var get = await http.GetAsync($"/indexes/{indexName}/docs?search=x&queryType=semantic&api-version=2024-07-01", cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }
}
