using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// An index that exists but has never had a document written answers queries with nothing,
/// as in Azure, instead of failing because its Lucene directory has not been created yet.
/// </summary>
public class EmptyIndexIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task NeverWrittenIndex_SearchesAndGetsReturnNothing()
    {
        var indexName = "empty-" + Guid.NewGuid().ToString("N")[..8];
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

        var client = factory.CreateSearchClient(indexName);

        var results = await client.SearchAsync<SearchDocument>("x", new Azure.Search.Documents.SearchOptions { IncludeTotalCount = true }, cancellationToken);
        Assert.Equal(0, results.Value.TotalCount);

        Assert.Equal(0, (await client.GetDocumentCountAsync(cancellationToken)).Value);

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            async () => await client.GetDocumentAsync<SearchDocument>("1", cancellationToken: cancellationToken));
        Assert.Equal(404, ex.Status);
    }
}
