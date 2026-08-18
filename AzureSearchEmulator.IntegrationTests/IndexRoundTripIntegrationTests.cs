using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for preserving index properties the emulator does not model
/// (issue #41), run against a containerized emulator.
/// </summary>
/// <remarks>
/// These go over raw HTTP rather than through the Azure Search SDK on purpose. The SDK's
/// <c>SearchIndex</c> model would silently normalize an unknown property away on the way out,
/// so a test written against it could not tell a definition the emulator preserved from one
/// the SDK reconstructed. Sending and reading the JSON directly is the only way to observe
/// what the emulator actually stored.
///
/// The bug this covers destroys data rather than merely returning the wrong answer: a client
/// that reads its index, edits one field and writes it back had its scoring profiles and
/// vector configuration deleted from the stored definition.
/// </remarks>
public class IndexRoundTripIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    /// <summary>
    /// An index definition exercising the unmodelled index-level and field-level properties
    /// the issue named.
    /// </summary>
    private static string IndexJson(string indexName) =>
        $$"""
        {
          "name": "{{indexName}}",
          "fields": [
            { "name": "id", "type": "Edm.String", "key": true },
            { "name": "description", "type": "Edm.String", "searchable": true },
            { "name": "embedding", "type": "Collection(Edm.Single)", "dimensions": 1536, "vectorSearchProfile": "vp" }
          ],
          "scoringProfiles": [
            { "name": "boostDescription", "text": { "weights": { "description": 2.5 } } }
          ],
          "corsOptions": { "allowedOrigins": ["*"], "maxAgeInSeconds": 300 },
          "similarity": { "@odata.type": "#Microsoft.Azure.Search.BM25Similarity", "k1": 1.2, "b": 0.75 },
          "vectorSearch": { "profiles": [{ "name": "vp", "algorithm": "hnsw" }] },
          "encryptionKey": { "keyVaultKeyName": "k", "keyVaultUri": "https://example.vault.azure.net" }
        }
        """;

    [Fact]
    public async Task CreateThenGet_PreservesUnmodelledProperties()
    {
        const string indexName = "test-roundtrip-create";
        var client = await CreateClientAsync();

        await CreateIndexAsync(client, indexName);

        var stored = await GetIndexAsync(client, indexName);

        Assert.Equal(2.5, stored["scoringProfiles"]?[0]?["text"]?["weights"]?["description"]?.GetValue<double>());
        Assert.Equal(300, stored["corsOptions"]?["maxAgeInSeconds"]?.GetValue<int>());
        Assert.Equal("hnsw", stored["vectorSearch"]?["profiles"]?[0]?["algorithm"]?.GetValue<string>());
        Assert.Equal("k", stored["encryptionKey"]?["keyVaultKeyName"]?.GetValue<string>());
        Assert.Equal("#Microsoft.Azure.Search.BM25Similarity",
            stored["similarity"]?["@odata.type"]?.GetValue<string>());

        await DeleteIndexAsync(client, indexName);
    }

    [Fact]
    public async Task CreateThenGet_PreservesUnmodelledFieldProperties()
    {
        const string indexName = "test-roundtrip-fields";
        var client = await CreateClientAsync();

        await CreateIndexAsync(client, indexName);

        var stored = await GetIndexAsync(client, indexName);
        var embedding = stored["fields"]?.AsArray()
            .FirstOrDefault(i => i?["name"]?.GetValue<string>() == "embedding");

        Assert.NotNull(embedding);
        Assert.Equal(1536, embedding["dimensions"]?.GetValue<int>());
        Assert.Equal("vp", embedding["vectorSearchProfile"]?.GetValue<string>());

        await DeleteIndexAsync(client, indexName);
    }

    /// <summary>
    /// The exact cycle from the issue: GET the definition, add a field to it, PUT it back, and
    /// confirm the properties the emulator does not understand are still there afterwards.
    /// </summary>
    [Fact]
    public async Task GetModifyPut_DoesNotStripUnmodelledProperties()
    {
        const string indexName = "test-roundtrip-modify";
        var client = await CreateClientAsync();

        await CreateIndexAsync(client, indexName);

        var definition = await GetIndexAsync(client, indexName);
        definition["fields"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "rating",
            ["type"] = "Edm.Double",
            ["filterable"] = true
        });

        var put = await client.PutAsync($"/indexes/{indexName}?api-version=2024-07-01",
            Json(definition.ToJsonString()), TestContext.Current.CancellationToken);
        Assert.True(put.IsSuccessStatusCode, await Describe(put));

        var stored = await GetIndexAsync(client, indexName);

        Assert.Contains(stored["fields"]!.AsArray(), i => i?["name"]?.GetValue<string>() == "rating");
        Assert.Equal(2.5, stored["scoringProfiles"]?[0]?["text"]?["weights"]?["description"]?.GetValue<double>());
        Assert.Equal("hnsw", stored["vectorSearch"]?["profiles"]?[0]?["algorithm"]?.GetValue<string>());
        var embedding = stored["fields"]?.AsArray()
            .FirstOrDefault(i => i?["name"]?.GetValue<string>() == "embedding");
        Assert.Equal(1536, embedding?["dimensions"]?.GetValue<int>());

        await DeleteIndexAsync(client, indexName);
    }

    /// <summary>
    /// Documents a known gap rather than a guarantee: the collection listing still serializes
    /// through OData, which emits only what the EDM model declares, so the properties preserved
    /// through the extension bag are absent there.
    /// </summary>
    /// <remarks>
    /// Asserted so the boundary is explicit and a future change to the listing endpoint is a
    /// deliberate decision rather than a silent one. If this test starts failing because the
    /// properties now appear, that is an improvement — update the test.
    ///
    /// The gap narrowed once already: <c>scoringProfiles</c> was listed here until issue #47
    /// made it a modelled property, at which point the EDM model began emitting it and it moved
    /// to the assertions below. Anything still named here is genuinely unmodelled.
    /// </remarks>
    [Fact]
    public async Task ListEndpoint_DoesNotYetIncludeUnmodelledProperties()
    {
        const string indexName = "test-roundtrip-listing";
        var client = await CreateClientAsync();

        await CreateIndexAsync(client, indexName);

        var response = await client.GetAsync("/indexes?api-version=2024-07-01",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var listed = JsonNode.Parse(body)?["value"]?.AsArray()
            .FirstOrDefault(i => i?["name"]?.GetValue<string>() == indexName);

        Assert.NotNull(listed);
        Assert.Null(listed["corsOptions"]);
        Assert.Null(listed["vectorSearch"]);

        // Modelled since issue #47, so the listing carries it like any other declared property.
        Assert.NotNull(listed["scoringProfiles"]);

        // The individual route is the one that carries the full definition.
        Assert.NotNull((await GetIndexAsync(client, indexName))["corsOptions"]);

        await DeleteIndexAsync(client, indexName);
    }

    /// <summary>
    /// The validation half of the issue: because the body was read off the stream rather than
    /// bound, <c>ModelState</c> was always empty and the <c>[Required]</c> attributes on
    /// <see cref="AzureSearchEmulator.Models.SearchField"/> never ran. A field missing its
    /// name must now be rejected rather than stored.
    /// </summary>
    [Fact]
    public async Task FieldMissingName_IsRejected()
    {
        const string indexName = "test-roundtrip-invalid-field";
        var client = await CreateClientAsync();

        var json = $$"""
            { "name": "{{indexName}}", "fields": [{ "type": "Edm.String", "key": true }] }
            """;

        var response = await client.PostAsync("/indexes?api-version=2024-07-01",
            Json(json), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FieldMissingType_IsRejected()
    {
        const string indexName = "test-roundtrip-invalid-type";
        var client = await CreateClientAsync();

        var json = $$"""
            { "name": "{{indexName}}", "fields": [{ "name": "id", "key": true }] }
            """;

        var response = await client.PostAsync("/indexes?api-version=2024-07-01",
            Json(json), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpClient> CreateClientAsync()
    {
        await factory.WaitUntilServingAsync();
        return factory.CreateHttpClient();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task CreateIndexAsync(HttpClient client, string indexName)
    {
        var response = await client.PostAsync("/indexes?api-version=2024-07-01",
            Json(IndexJson(indexName)), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, await Describe(response));
    }

    private static async Task<JsonObject> GetIndexAsync(HttpClient client, string indexName)
    {
        var response = await client.GetAsync($"/indexes/{indexName}?api-version=2024-07-01",
            TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, await Describe(response));

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonNode.Parse(body)?.AsObject()
               ?? throw new InvalidOperationException($"Index response was not a JSON object: {body}");
    }

    private static async Task DeleteIndexAsync(HttpClient client, string indexName)
        => await client.DeleteAsync($"/indexes/{indexName}?api-version=2024-07-01",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Includes the response body in the assertion message, so a failure names the reason the
    /// emulator gave rather than only the status code.
    /// </summary>
    private static async Task<string> Describe(HttpResponseMessage response)
        => $"{(int)response.StatusCode} {response.StatusCode}: " +
           await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
}
