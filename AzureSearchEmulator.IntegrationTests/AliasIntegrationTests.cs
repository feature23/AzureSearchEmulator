using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Alias CRUD and resolution against a containerized emulator. The pinned SDK predates aliases,
/// so the alias routes are driven over raw HTTP; document operations go through the SDK's
/// <c>SearchClient</c>, addressed by the alias name.
/// </summary>
public class AliasIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private const string ApiVersion = "api-version=2025-11-01-preview";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private async Task CreateIndexAsync(string name)
    {
        await factory.CreateSearchIndexClient().CreateIndexAsync(
            new SearchIndex(name)
            {
                Fields =
                {
                    new SearchField("id", SearchFieldDataType.String) { IsKey = true },
                    new SearchField("name", SearchFieldDataType.String) { IsSearchable = true },
                }
            },
            Token);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string AliasBody(string name, params string[] indexes) =>
        JsonSerializer.Serialize(new { name, indexes });

    [Fact]
    public async Task Alias_CrudLifecycle()
    {
        var index = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();

        var put = await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var get = await http.GetAsync($"/aliases('{alias}')?{ApiVersion}", Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Token));
        Assert.Equal(alias, doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(index, doc.RootElement.GetProperty("indexes")[0].GetString());

        var list = await http.GetStringAsync($"/aliases?{ApiVersion}", Token);
        Assert.Contains(alias, list);

        var update = await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var delete = await http.DeleteAsync($"/aliases('{alias}')?{ApiVersion}", Token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/aliases('{alias}')?{ApiVersion}", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/aliases('{alias}')?{ApiVersion}", Token)).StatusCode);
    }

    [Fact]
    public async Task Alias_ValidationErrors()
    {
        var index = Unique("alias-idx");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        var alias = Unique("alias");

        // Missing target, two targets, and a target that does not exist
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index, index)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, "missing-index")), Token)).StatusCode);

        // The alias name may not be an existing index's
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{index}')?{ApiVersion}", Json(AliasBody(index, index)), Token)).StatusCode);

        // Name in the body must match the URL
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody("other", index)), Token)).StatusCode);

        // POST of an existing alias conflicts
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsync($"/aliases?{ApiVersion}", Json(AliasBody(alias, index)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/aliases?{ApiVersion}", Json(AliasBody(alias, index)), Token)).StatusCode);
    }

    [Fact]
    public async Task Index_CannotBeCreatedWithAnAliasName()
    {
        var index = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(() => CreateIndexAsync(alias));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task DocumentRoutes_ResolveAliasAndFollowRepoint()
    {
        var first = Unique("alias-idx");
        var second = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(first);
        await CreateIndexAsync(second);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, first)), Token);

        // Index through the alias
        var indexed = await http.PostAsync(
            $"/indexes/{alias}/docs/search.index?api-version=2024-07-01",
            Json("""{ "value": [ { "@search.action": "upload", "id": "1", "name": "hello world" } ] }"""),
            Token);
        Assert.Equal(HttpStatusCode.OK, indexed.StatusCode);

        var client = factory.CreateSearchClient(alias);
        Assert.Equal(1, (await client.GetDocumentCountAsync(Token)).Value);
        Assert.Equal("hello world", (await client.GetDocumentAsync<Dictionary<string, object>>("1", cancellationToken: Token)).Value["name"].ToString());
        Assert.Equal(1, (await client.SearchAsync<Dictionary<string, object>>("hello", cancellationToken: Token)).Value.GetResults().Count());

        // The data landed in the first index, not under the alias name
        Assert.Equal(1, (await factory.CreateSearchClient(first).GetDocumentCountAsync(Token)).Value);
        Assert.Equal(0, (await factory.CreateSearchClient(second).GetDocumentCountAsync(Token)).Value);

        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, second)), Token);
        Assert.Equal(0, (await client.GetDocumentCountAsync(Token)).Value);
    }

    [Fact]
    public async Task DocumentRoutes_ReturnNotFoundWhenTargetIsDeleted()
    {
        var index = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);

        await factory.CreateSearchIndexClient().DeleteIndexAsync(index, Token);

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            async () => await factory.CreateSearchClient(alias).GetDocumentCountAsync(Token));
        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task IndexManagementRoutes_DoNotResolveAliases()
    {
        var index = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/indexes('{alias}')?{ApiVersion}", Token)).StatusCode);
    }

    [Fact]
    public async Task ServiceStatistics_CountAliases()
    {
        var index = Unique("alias-idx");
        var alias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{alias}')?{ApiVersion}", Json(AliasBody(alias, index)), Token);

        using var stats = JsonDocument.Parse(await http.GetStringAsync($"/servicestats?{ApiVersion}", Token));

        Assert.True(stats.RootElement.GetProperty("counters").GetProperty("aliasesCount").GetProperty("usage").GetInt32() >= 1);
    }
}
