using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Alias CRUD and resolution against a containerized emulator, through the SDK's
/// <c>SearchIndexClient</c> alias operations. Raw HTTP is kept only for requests the SDK cannot
/// make: a body whose name differs from the URL, <c>If-None-Match: *</c>, and an index update
/// addressed by the alias name.
/// </summary>
public class AliasIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    private const string ApiVersion = "api-version=2026-04-01";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    private readonly SearchIndexClient _indexClient = factory.CreateSearchIndexClient();

    private async Task CreateIndexAsync(string name)
    {
        await _indexClient.CreateIndexAsync(
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

    private Task<Response<SearchAlias>> CreateAliasAsync(string aliasName, params string[] indexes) =>
        _indexClient.CreateAliasAsync(new SearchAlias(aliasName, indexes), Token);

    private static async Task<int> StatusOf(Func<Task> request) =>
        (await Assert.ThrowsAsync<RequestFailedException>(request)).Status;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Alias_CrudLifecycle()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);

        var created = await CreateAliasAsync(aliasName, index);
        Assert.Equal(201, created.GetRawResponse().Status);

        var fetched = (await _indexClient.GetAliasAsync(aliasName, Token)).Value;
        Assert.Equal(aliasName, fetched.Name);
        Assert.Equal([index], fetched.Indexes);

        var names = new List<string>();
        await foreach (var listed in _indexClient.GetAliasesAsync(Token))
        {
            names.Add(listed.Name);
        }
        Assert.Contains(aliasName, names);

        var updated = await _indexClient.CreateOrUpdateAliasAsync(fetched, cancellationToken: Token);
        Assert.Equal(200, updated.GetRawResponse().Status);

        var deleted = await _indexClient.DeleteAliasAsync(aliasName, cancellationToken: Token);
        Assert.Equal(204, deleted.Status);

        Assert.Equal(404, await StatusOf(() => _indexClient.GetAliasAsync(aliasName, Token)));
        Assert.Equal(404, await StatusOf(() => _indexClient.DeleteAliasAsync(aliasName, cancellationToken: Token)));
    }

    [Fact]
    public async Task Alias_ValidationErrors()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);

        // Missing target, two targets, and a target that does not exist
        Assert.Equal(400, await StatusOf(() => CreateAliasAsync(aliasName)));
        Assert.Equal(400, await StatusOf(() => CreateAliasAsync(aliasName, index, index)));
        Assert.Equal(400, await StatusOf(() => CreateAliasAsync(aliasName, "missing-index")));

        // The alias name may not be an existing index's
        Assert.Equal(400, await StatusOf(() => CreateAliasAsync(index, index)));

        // Name in the body must match the URL, which the SDK always keeps in step
        using var http = factory.CreateHttpClient();
        var mismatched = await http.PutAsync(
            $"/aliases('{aliasName}')?{ApiVersion}",
            Json(JsonSerializer.Serialize(new { name = "other", indexes = new[] { index } })),
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);

        // Creating an alias that exists conflicts
        await CreateAliasAsync(aliasName, index);
        Assert.Equal(409, await StatusOf(() => CreateAliasAsync(aliasName, index)));
    }

    [Fact]
    public async Task Index_CannotBeCreatedWithAnAliasName()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);
        await CreateAliasAsync(aliasName, index);

        Assert.Equal(409, await StatusOf(() => CreateIndexAsync(aliasName)));
    }

    [Fact]
    public async Task DocumentRoutes_ResolveAliasAndFollowRepoint()
    {
        var first = Unique("alias-idx");
        var second = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(first);
        await CreateIndexAsync(second);
        await CreateAliasAsync(aliasName, first);

        // Index through the alias
        var client = factory.CreateSearchClient(aliasName);
        await client.UploadDocumentsAsync([new Dictionary<string, object> { ["id"] = "1", ["name"] = "hello world" }], cancellationToken: Token);

        Assert.Equal(1, (await client.GetDocumentCountAsync(Token)).Value);
        Assert.Equal("hello world", (await client.GetDocumentAsync<Dictionary<string, object>>("1", cancellationToken: Token)).Value["name"].ToString());
        Assert.Single((await client.SearchAsync<Dictionary<string, object>>("hello", cancellationToken: Token)).Value.GetResults());

        // The data landed in the first index, not under the alias name
        Assert.Equal(1, (await factory.CreateSearchClient(first).GetDocumentCountAsync(Token)).Value);
        Assert.Equal(0, (await factory.CreateSearchClient(second).GetDocumentCountAsync(Token)).Value);

        await _indexClient.CreateOrUpdateAliasAsync(new SearchAlias(aliasName, second), cancellationToken: Token);
        Assert.Equal(0, (await client.GetDocumentCountAsync(Token)).Value);
    }

    [Fact]
    public async Task Index_CannotBeDeletedWhileAnAliasTargetsIt()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);
        await CreateAliasAsync(aliasName, index);

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() => _indexClient.DeleteIndexAsync(index, Token));
        Assert.Equal(400, ex.Status);
        Assert.Contains(aliasName, ex.Message);

        // Still there, and still reachable through the alias
        Assert.Equal(0, (await factory.CreateSearchClient(aliasName).GetDocumentCountAsync(Token)).Value);

        await _indexClient.DeleteAliasAsync(aliasName, cancellationToken: Token);
        await _indexClient.DeleteIndexAsync(index, Token);
    }

    [Fact]
    public async Task IndexRoutes_GetAndUpdateThroughAlias()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);
        await CreateAliasAsync(aliasName, index);

        // Get returns the target's definition, under its own name
        var fetched = (await _indexClient.GetIndexAsync(aliasName, Token)).Value;
        Assert.Equal(index, fetched.Name);

        // Update through the alias changes the target and creates nothing under the alias name.
        // The SDK addresses an update by the name in the definition, so this one goes by hand.
        using var http = factory.CreateHttpClient();
        var update = await http.PutAsync($"/indexes('{aliasName}')?{ApiVersion}",
            Json(JsonSerializer.Serialize(new
            {
                name = aliasName,
                fields = new object[]
                {
                    new { name = "id", type = "Edm.String", key = true },
                    new { name = "name", type = "Edm.String", searchable = true },
                    new { name = "extra", type = "Edm.String", searchable = true },
                }
            })),
            Token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var target = (await _indexClient.GetIndexAsync(index, Token)).Value;
        Assert.Contains(target.Fields, f => f.Name == "extra");

        var indexNames = new List<string>();
        await foreach (var listed in _indexClient.GetIndexesAsync(Token))
        {
            indexNames.Add(listed.Name);
        }
        Assert.DoesNotContain(aliasName, indexNames);
    }

    [Fact]
    public async Task IndexRoutes_DeleteDoesNotResolveAlias()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);
        await CreateAliasAsync(aliasName, index);

        Assert.Equal(404, await StatusOf(() => _indexClient.DeleteIndexAsync(aliasName, Token)));
        Assert.Equal(index, (await _indexClient.GetIndexAsync(index, Token)).Value.Name);
    }

    [Fact]
    public async Task Alias_HonoursETagPreconditions()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);

        var created = (await CreateAliasAsync(aliasName, index)).Value;
        Assert.NotNull(created.ETag);

        // If-None-Match: * refuses to overwrite an alias that exists. The SDK never sends it.
        using var http = factory.CreateHttpClient();
        var createOnly = new HttpRequestMessage(HttpMethod.Put, $"/aliases('{aliasName}')?{ApiVersion}")
        {
            Content = Json(JsonSerializer.Serialize(new { name = aliasName, indexes = new[] { index } }))
        };
        createOnly.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await http.SendAsync(createOnly, Token)).StatusCode);

        // The current ETag is accepted and renewed; the old one is then stale
        var updated = (await _indexClient.CreateOrUpdateAliasAsync(created, onlyIfUnchanged: true, cancellationToken: Token)).Value;
        Assert.NotEqual(created.ETag, updated.ETag);

        Assert.Equal(412, await StatusOf(() => _indexClient.CreateOrUpdateAliasAsync(created, onlyIfUnchanged: true, cancellationToken: Token)));
        Assert.Equal(412, await StatusOf(() => _indexClient.DeleteAliasAsync(created, onlyIfUnchanged: true, cancellationToken: Token)));

        var deleted = await _indexClient.DeleteAliasAsync(updated, onlyIfUnchanged: true, cancellationToken: Token);
        Assert.Equal(204, deleted.Status);
    }

    [Fact]
    public async Task Alias_NameMustFollowIndexNamingRules()
    {
        var index = Unique("alias-idx");
        await CreateIndexAsync(index);

        foreach (var name in new[] { "Upper-case", "a", "double--dash", "double__underscore" })
        {
            Assert.Equal(400, await StatusOf(() => CreateAliasAsync(name, index)));
        }
    }

    [Fact]
    public async Task ServiceStatistics_CountAliases()
    {
        var index = Unique("alias-idx");
        var aliasName = Unique("alias");
        await CreateIndexAsync(index);
        await CreateAliasAsync(aliasName, index);

        var stats = (await _indexClient.GetServiceStatisticsAsync(Token)).Value;

        Assert.True(stats.Counters.AliasCounter.Usage >= 1);
    }
}
