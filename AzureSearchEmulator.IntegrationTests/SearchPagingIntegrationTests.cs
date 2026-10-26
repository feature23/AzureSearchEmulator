using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Covers <c>@search.nextPageParameters</c>, which the Azure Search SDK follows to read past
/// the first 50 results of a search that leaves out <c>top</c>.
/// </summary>
public class SearchPagingIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private const int DocumentCount = 120;

    [Fact]
    public async Task SearchWithoutSize_EnumeratesEveryResultAcrossPages()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-paging-all");

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", new SearchOptions { OrderBy = { "Id" } }, TestContext.Current.CancellationToken);

        var pageSizes = new List<int>();
        var ids = new List<string>();

        await foreach (var page in response.Value.GetResultsAsync().AsPages())
        {
            pageSizes.Add(page.Values.Count);
            ids.AddRange(page.Values.Select(r => (string)r.Document["Id"]));
        }

        Assert.Equal([50, 50, 20], pageSizes);
        Assert.Equal(Enumerable.Range(0, DocumentCount).Select(i => i.ToString("D3")), ids);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SearchWithSize_ReturnsOnlyThatManyResults()
    {
        var (indexClient, searchClient, indexName) = await SetUpAsync("test-paging-size");

        var response = await searchClient.SearchAsync<SearchDocument>(
            "*", new SearchOptions { Size = 50 }, TestContext.Current.CancellationToken);

        var count = 0;

        await foreach (var _ in response.Value.GetResultsAsync())
        {
            count++;
        }

        Assert.Equal(50, count);

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The REST shape, for clients that read the continuation themselves instead of through the
    /// SDK: the same request with <c>skip</c> moved on, and still no <c>top</c>.
    /// </summary>
    [Fact]
    public async Task SearchPostWithoutTop_ReturnsNextPageParametersAndNextLink()
    {
        var (indexClient, _, indexName) = await SetUpAsync("test-paging-rest");

        using var http = factory.CreateHttpClient();

        using var httpResponse = await http.PostAsJsonAsync(
            $"indexes/{indexName}/docs/search?api-version=2024-07-01",
            new JsonObject { ["search"] = "*", ["filter"] = "Id ne 'none'" },
            TestContext.Current.CancellationToken);

        httpResponse.EnsureSuccessStatusCode();

        var body = (await httpResponse.Content.ReadFromJsonAsync<JsonObject>(TestContext.Current.CancellationToken))!;
        var next = body["@search.nextPageParameters"]!.AsObject();

        Assert.Equal(50, next["skip"]!.GetValue<int>());
        Assert.False(next.ContainsKey("top"));
        Assert.Equal("*", next["search"]!.GetValue<string>());
        Assert.Equal("Id ne 'none'", next["filter"]!.GetValue<string>());
        Assert.EndsWith(
            $"/indexes('{indexName}')/docs/search.post.search?api-version=2024-07-01",
            body["@odata.nextLink"]!.GetValue<string>());

        await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
    }

    private async Task<(SearchIndexClient IndexClient, SearchClient SearchClient, string IndexName)> SetUpAsync(
        string indexName)
    {
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        try
        {
            await indexClient.DeleteIndexAsync(indexName, TestContext.Current.CancellationToken);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // expected
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
                new SearchableField("Name"),
            ]
        };

        await indexClient.CreateIndexAsync(index, TestContext.Current.CancellationToken);

        var documents = Enumerable.Range(0, DocumentCount)
            .Select(i => new SearchDocument { ["Id"] = i.ToString("D3"), ["Name"] = "Document " + i })
            .ToList();

        await searchClient.IndexDocumentsAsync(
            IndexDocumentsBatch.Upload(documents),
            cancellationToken: TestContext.Current.CancellationToken);

        return (indexClient, searchClient, indexName);
    }
}
