using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Alias CRUD and resolution against a containerized emulator. The pinned SDK (11.7.0) has no
/// alias operations, so the alias routes are driven over raw HTTP; document and index operations
/// go through the SDK's clients, addressed by the alias name.
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
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();

        var put = await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var get = await http.GetAsync($"/aliases('{searchAlias}')?{ApiVersion}", Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync(Token));
        Assert.Equal(searchAlias, doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(index, doc.RootElement.GetProperty("indexes")[0].GetString());

        var list = await http.GetStringAsync($"/aliases?{ApiVersion}", Token);
        Assert.Contains(searchAlias, list);

        var update = await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var delete = await http.DeleteAsync($"/aliases('{searchAlias}')?{ApiVersion}", Token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/aliases('{searchAlias}')?{ApiVersion}", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/aliases('{searchAlias}')?{ApiVersion}", Token)).StatusCode);
    }

    [Fact]
    public async Task Alias_ValidationErrors()
    {
        var index = Unique("alias-idx");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        var searchAlias = Unique("alias");

        // Missing target, two targets, and a target that does not exist
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index, index)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, "missing-index")), Token)).StatusCode);

        // The alias name may not be an existing index's
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{index}')?{ApiVersion}", Json(AliasBody(index, index)), Token)).StatusCode);

        // Name in the body must match the URL
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody("other", index)), Token)).StatusCode);

        // POST of an existing alias conflicts
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsync($"/aliases?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/aliases?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token)).StatusCode);
    }

    [Fact]
    public async Task Index_CannotBeCreatedWithAnAliasName()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(() => CreateIndexAsync(searchAlias));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task DocumentRoutes_ResolveAliasAndFollowRepoint()
    {
        var first = Unique("alias-idx");
        var second = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(first);
        await CreateIndexAsync(second);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, first)), Token);

        // Index through the alias
        var indexed = await http.PostAsync(
            $"/indexes/{searchAlias}/docs/search.index?api-version=2024-07-01",
            Json("""{ "value": [ { "@search.action": "upload", "id": "1", "name": "hello world" } ] }"""),
            Token);
        Assert.Equal(HttpStatusCode.OK, indexed.StatusCode);

        var client = factory.CreateSearchClient(searchAlias);
        Assert.Equal(1, (await client.GetDocumentCountAsync(Token)).Value);
        Assert.Equal("hello world", (await client.GetDocumentAsync<Dictionary<string, object>>("1", cancellationToken: Token)).Value["name"].ToString());
        Assert.Equal(1, (await client.SearchAsync<Dictionary<string, object>>("hello", cancellationToken: Token)).Value.GetResults().Count());

        // The data landed in the first index, not under the alias name
        Assert.Equal(1, (await factory.CreateSearchClient(first).GetDocumentCountAsync(Token)).Value);
        Assert.Equal(0, (await factory.CreateSearchClient(second).GetDocumentCountAsync(Token)).Value);

        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, second)), Token);
        Assert.Equal(0, (await client.GetDocumentCountAsync(Token)).Value);
    }

    [Fact]
    public async Task Index_CannotBeDeletedWhileAnAliasTargetsIt()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);
        var indexClient = factory.CreateSearchIndexClient();

        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(() => indexClient.DeleteIndexAsync(index, Token));
        Assert.Equal(400, ex.Status);
        Assert.Contains(searchAlias, ex.Message);

        // Still there, and still reachable through the alias
        Assert.Equal(0, (await factory.CreateSearchClient(searchAlias).GetDocumentCountAsync(Token)).Value);

        await http.DeleteAsync($"/aliases('{searchAlias}')?{ApiVersion}", Token);
        await indexClient.DeleteIndexAsync(index, Token);
    }

    [Fact]
    public async Task IndexRoutes_GetAndUpdateThroughAlias()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);
        var indexClient = factory.CreateSearchIndexClient();

        // Get returns the target's definition, under its own name
        var fetched = (await indexClient.GetIndexAsync(searchAlias, Token)).Value;
        Assert.Equal(index, fetched.Name);

        // Update through the alias changes the target and creates nothing under the alias name
        var update = await http.PutAsync($"/indexes('{searchAlias}')?{ApiVersion}",
            Json(JsonSerializer.Serialize(new
            {
                name = searchAlias,
                fields = new object[]
                {
                    new { name = "id", type = "Edm.String", key = true },
                    new { name = "name", type = "Edm.String", searchable = true },
                    new { name = "extra", type = "Edm.String", searchable = true },
                }
            })),
            Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var target = (await indexClient.GetIndexAsync(index, Token)).Value;
        Assert.Contains(target.Fields, f => f.Name == "extra");
        Assert.DoesNotContain(searchAlias, await http.GetStringAsync($"/indexes?{ApiVersion}", Token));
    }

    [Fact]
    public async Task IndexRoutes_DeleteDoesNotResolveAlias()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);

        Assert.Equal(HttpStatusCode.NotFound, (await http.DeleteAsync($"/indexes('{searchAlias}')?{ApiVersion}", Token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/indexes('{index}')?{ApiVersion}", Token)).StatusCode);
    }

    [Fact]
    public async Task Alias_HonoursETagPreconditions()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        var url = $"/aliases('{searchAlias}')?{ApiVersion}";

        var created = await http.PutAsync(url, Json(AliasBody(searchAlias, index)), Token);
        var etag = created.Headers.ETag!.Tag;
        using (var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Token)))
        {
            Assert.Equal(etag, body.RootElement.GetProperty("@odata.etag").GetString());
        }

        // If-None-Match: * refuses to overwrite an alias that exists
        var createOnly = new HttpRequestMessage(HttpMethod.Put, url) { Content = Json(AliasBody(searchAlias, index)) };
        createOnly.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await http.SendAsync(createOnly, Token)).StatusCode);

        // The current ETag is accepted and renewed; the old one is then stale
        var current = new HttpRequestMessage(HttpMethod.Put, url) { Content = Json(AliasBody(searchAlias, index)) };
        current.Headers.TryAddWithoutValidation("If-Match", etag);
        var updated = await http.SendAsync(current, Token);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.NotEqual(etag, updated.Headers.ETag!.Tag);

        var stale = new HttpRequestMessage(HttpMethod.Delete, url);
        stale.Headers.TryAddWithoutValidation("If-Match", etag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await http.SendAsync(stale, Token)).StatusCode);

        var fresh = new HttpRequestMessage(HttpMethod.Delete, url);
        fresh.Headers.TryAddWithoutValidation("If-Match", updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(fresh, Token)).StatusCode);
    }

    [Fact]
    public async Task Alias_NameMustFollowIndexNamingRules()
    {
        var index = Unique("alias-idx");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();

        foreach (var name in new[] { "Upper-case", "a", "double--dash", "double__underscore" })
        {
            Assert.Equal(HttpStatusCode.BadRequest,
                (await http.PostAsync($"/aliases?{ApiVersion}", Json(AliasBody(name, index)), Token)).StatusCode);
        }
    }

    [Fact]
    public async Task ServiceStatistics_CountAliases()
    {
        var index = Unique("alias-idx");
        var searchAlias = Unique("alias");
        await CreateIndexAsync(index);
        using var http = factory.CreateHttpClient();
        await http.PutAsync($"/aliases('{searchAlias}')?{ApiVersion}", Json(AliasBody(searchAlias, index)), Token);

        using var stats = JsonDocument.Parse(await http.GetStringAsync($"/servicestats?{ApiVersion}", Token));

        Assert.True(stats.RootElement.GetProperty("counters").GetProperty("aliasesCount").GetProperty("usage").GetInt32() >= 1);
    }
}
