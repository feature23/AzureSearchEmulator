using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the wire format of a vector search configuration, which has to match Azure's
/// exactly for a definition to survive a round-trip through the Azure SDK (issue #46).
/// </summary>
public class VectorSearchJsonTests
{
    /// <summary>
    /// The same options the app serializes with.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string VectorIndexJson =
        """
        {
          "name": "vectors",
          "fields": [
            { "name": "id", "type": "Edm.String", "key": true },
            {
              "name": "embedding",
              "type": "Collection(Edm.Single)",
              "searchable": true,
              "retrievable": false,
              "filterable": false,
              "dimensions": 1536,
              "vectorSearchProfile": "vp"
            }
          ],
          "vectorSearch": {
            "algorithms": [
              {
                "name": "hnswAlgo",
                "kind": "hnsw",
                "hnswParameters": { "m": 4, "efConstruction": 400, "efSearch": 500, "metric": "cosine" }
              },
              {
                "name": "exhaustiveAlgo",
                "kind": "exhaustiveKnn",
                "exhaustiveKnnParameters": { "metric": "dotProduct" }
              }
            ],
            "profiles": [
              { "name": "vp", "algorithm": "hnswAlgo" },
              { "name": "exhaustive", "algorithm": "exhaustiveAlgo" }
            ]
          }
        }
        """;

    private static SearchIndex Deserialize(string json)
        => JsonSerializer.Deserialize<SearchIndex>(json, Options)
           ?? throw new InvalidOperationException("Index deserialized to null");

    private static JsonObject RoundTrip(string json)
        => JsonNode.Parse(JsonSerializer.Serialize(Deserialize(json), Options))!.AsObject();

    [Fact]
    public void VectorSearch_Deserializes()
    {
        var index = Deserialize(VectorIndexJson);

        Assert.NotNull(index.VectorSearch);
        Assert.Equal(2, index.VectorSearch.Algorithms.Count);
        Assert.Equal(2, index.VectorSearch.Profiles.Count);
    }

    [Fact]
    public void Algorithm_KindAndParameters_Deserialize()
    {
        var vectorSearch = Deserialize(VectorIndexJson).VectorSearch!;

        var hnsw = vectorSearch.FindAlgorithm("hnswAlgo")!;
        Assert.Equal(VectorSearchAlgorithmKind.Hnsw, hnsw.Kind);
        Assert.Equal(4, hnsw.HnswParameters?.M);
        Assert.Equal(400, hnsw.HnswParameters?.EfConstruction);
        Assert.Equal(500, hnsw.HnswParameters?.EfSearch);

        var exhaustive = vectorSearch.FindAlgorithm("exhaustiveAlgo")!;
        Assert.Equal(VectorSearchAlgorithmKind.ExhaustiveKnn, exhaustive.Kind);
        Assert.Equal(VectorSearchMetric.DotProduct, exhaustive.ExhaustiveKnnParameters?.Metric);
    }

    [Fact]
    public void Field_VectorProperties_Deserialize()
    {
        var field = Deserialize(VectorIndexJson).Fields[1];

        Assert.Equal("Collection(Edm.Single)", field.Type);
        Assert.Equal(1536, field.Dimensions);
        Assert.Equal("vp", field.VectorSearchProfile);
    }

    /// <summary>
    /// The metric is what a query actually depends on, and it lives one level deeper than the
    /// profile a field names, so resolving it end to end is worth asserting directly.
    /// </summary>
    [Theory]
    [InlineData("vp", VectorSearchMetric.Cosine)]
    [InlineData("exhaustive", VectorSearchMetric.DotProduct)]
    public void ResolveMetric_FollowsProfileToAlgorithm(string profile, VectorSearchMetric expected)
    {
        var vectorSearch = Deserialize(VectorIndexJson).VectorSearch!;

        Assert.Equal(expected, vectorSearch.ResolveMetric(profile));
    }

    [Fact]
    public void ResolveMetric_IsNull_ForUnknownProfile()
    {
        var vectorSearch = Deserialize(VectorIndexJson).VectorSearch!;

        Assert.Null(vectorSearch.ResolveMetric("nope"));
    }

    /// <summary>
    /// Azure omits the metric when it is the default, so a definition that never mentions
    /// cosine still has to resolve to it.
    /// </summary>
    [Fact]
    public void Metric_DefaultsToCosine_WhenAbsent()
    {
        const string json =
            """
            {
              "name": "vectors",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "vectorSearch": {
                "algorithms": [{ "name": "a", "kind": "hnsw" }],
                "profiles": [{ "name": "p", "algorithm": "a" }]
              }
            }
            """;

        Assert.Equal(VectorSearchMetric.Cosine, Deserialize(json).VectorSearch!.ResolveMetric("p"));
    }

    /// <summary>
    /// Names are matched the way Azure matches them, so a field may differ in case from the
    /// profile it binds to.
    /// </summary>
    [Fact]
    public void Lookups_AreCaseInsensitive()
    {
        var vectorSearch = Deserialize(VectorIndexJson).VectorSearch!;

        Assert.NotNull(vectorSearch.FindProfile("VP"));
        Assert.NotNull(vectorSearch.FindAlgorithm("HNSWALGO"));
    }

    [Fact]
    public void VectorSearch_SurvivesRoundTrip()
    {
        var result = RoundTrip(VectorIndexJson);
        var vectorSearch = result["vectorSearch"];

        Assert.Equal("hnsw", vectorSearch?["algorithms"]?[0]?["kind"]?.GetValue<string>());
        Assert.Equal("cosine", vectorSearch?["algorithms"]?[0]?["hnswParameters"]?["metric"]?.GetValue<string>());
        Assert.Equal("exhaustiveKnn", vectorSearch?["algorithms"]?[1]?["kind"]?.GetValue<string>());
        Assert.Equal("dotProduct", vectorSearch?["algorithms"]?[1]?["exhaustiveKnnParameters"]?["metric"]?.GetValue<string>());
        Assert.Equal("vp", vectorSearch?["profiles"]?[0]?["name"]?.GetValue<string>());
        Assert.Equal("hnswAlgo", vectorSearch?["profiles"]?[0]?["algorithm"]?.GetValue<string>());
    }

    /// <summary>
    /// The tuning knobs are ignored when answering a query, but dropping them would delete
    /// configuration from a definition the caller wrote, which is what issue #41 set out to
    /// stop.
    /// </summary>
    [Fact]
    public void IgnoredHnswParameters_SurviveRoundTrip()
    {
        var parameters = RoundTrip(VectorIndexJson)["vectorSearch"]?["algorithms"]?[0]?["hnswParameters"];

        Assert.Equal(4, parameters?["m"]?.GetValue<int>());
        Assert.Equal(400, parameters?["efConstruction"]?.GetValue<int>());
        Assert.Equal(500, parameters?["efSearch"]?.GetValue<int>());
    }

    [Fact]
    public void FieldVectorProperties_SurviveRoundTrip()
    {
        var field = RoundTrip(VectorIndexJson)["fields"]?[1];

        Assert.Equal(1536, field?["dimensions"]?.GetValue<int>());
        Assert.Equal("vp", field?["vectorSearchProfile"]?.GetValue<string>());
    }

    /// <summary>
    /// An index with no vector configuration must not grow an empty <c>vectorSearch</c> object,
    /// which would be a change to a definition the caller did not ask for.
    /// </summary>
    [Fact]
    public void NonVectorIndex_GainsNoVectorSearchProperty()
    {
        const string json =
            """
            {
              "name": "hotels",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }]
            }
            """;

        var result = RoundTrip(json);

        Assert.False(result.ContainsKey("vectorSearch"));
        Assert.Null(result["fields"]?[0]?["dimensions"]);
        Assert.Null(result["fields"]?[0]?["vectorSearchProfile"]);
    }

    /// <summary>
    /// An unrecognized enum value must be reported rather than silently becoming the default,
    /// for the reason given on <see cref="CamelCaseEnumConverter{TEnum}"/>.
    /// </summary>
    [Theory]
    [InlineData("\"kind\": \"hnwsw\"", "hnwsw")]
    [InlineData("\"kind\": \"hnsw\", \"hnswParameters\": { \"metric\": \"cosign\" }", "cosign")]
    public void UnrecognizedEnumValue_IsReported(string algorithmJson, string badValue)
    {
        var json =
            $$"""
            {
              "name": "vectors",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "vectorSearch": { "algorithms": [{ "name": "a", {{algorithmJson}} }] }
            }
            """;

        var ex = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.Contains(badValue, ex.Message);
    }

    /// <summary>
    /// Modelling <c>vectorSearch</c> means anything nested inside it that is not declared would
    /// be dropped, where before it rode through the index-level extension bag and survived by
    /// default. <c>vectorizers</c> and <c>compressions</c> are the two that matter: a client
    /// that read a real-service definition, changed one field and wrote it back would otherwise
    /// find them deleted — the loss issue #41 set out to stop.
    /// </summary>
    [Fact]
    public void UnmodelledVectorSearchProperties_SurviveRoundTrip()
    {
        const string json =
            """
            {
              "name": "vectors",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "vectorSearch": {
                "algorithms": [{ "name": "a", "kind": "hnsw" }],
                "profiles": [{ "name": "p", "algorithm": "a" }],
                "vectorizers": [
                  { "name": "vz", "kind": "azureOpenAI",
                    "azureOpenAIParameters": { "resourceUri": "https://example.openai.azure.com" } }
                ],
                "compressions": [{ "name": "cz", "kind": "scalarQuantization" }]
              }
            }
            """;

        var vectorSearch = RoundTrip(json)["vectorSearch"];

        Assert.NotNull(vectorSearch?["vectorizers"]);
        Assert.NotNull(vectorSearch?["compressions"]);

        // Structure intact, not merely present: a client has to be able to use what comes back.
        Assert.Equal("azureOpenAI", vectorSearch?["vectorizers"]?[0]?["kind"]?.GetValue<string>());
        Assert.Equal(
            "https://example.openai.azure.com",
            vectorSearch?["vectorizers"]?[0]?["azureOpenAIParameters"]?["resourceUri"]?.GetValue<string>());
    }

    /// <summary>
    /// The bag applies per type rather than recursively, so each level of the vector
    /// configuration needs its own.
    /// </summary>
    [Fact]
    public void UnmodelledPropertiesNestedDeeper_SurviveRoundTrip()
    {
        const string json =
            """
            {
              "name": "vectors",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "vectorSearch": {
                "algorithms": [
                  { "name": "a", "kind": "hnsw",
                    "hnswParameters": { "metric": "cosine", "somethingNew": 7 },
                    "unknownOnAlgorithm": true }
                ],
                "profiles": [
                  { "name": "p", "algorithm": "a", "unknownOnProfile": "kept" }
                ]
              }
            }
            """;

        var vectorSearch = RoundTrip(json)["vectorSearch"];

        Assert.Equal(7, vectorSearch?["algorithms"]?[0]?["hnswParameters"]?["somethingNew"]?.GetValue<int>());
        Assert.True(vectorSearch?["algorithms"]?[0]?["unknownOnAlgorithm"]?.GetValue<bool>());
        Assert.Equal("kept", vectorSearch?["profiles"]?[0]?["unknownOnProfile"]?.GetValue<string>());
    }

    /// <summary>
    /// <c>hamming</c> is a real Azure metric, restricted by its specification to bit-packed
    /// binary vectors — an element type the emulator does not support. Reporting it the way a
    /// typo is reported would send someone hunting for a spelling mistake in a value they spelled
    /// correctly, so the message names the actual reason.
    /// </summary>
    [Fact]
    public void HammingMetric_IsReportedAsUnsupportedRatherThanUnrecognized()
    {
        const string json =
            """
            {
              "name": "vectors",
              "fields": [{ "name": "id", "type": "Edm.String", "key": true }],
              "vectorSearch": {
                "algorithms": [
                  { "name": "a", "kind": "hnsw", "hnswParameters": { "metric": "hamming" } }
                ]
              }
            }
            """;

        var ex = Assert.Throws<JsonException>(() => Deserialize(json));

        Assert.Contains("bit-packed binary", ex.Message);
        Assert.DoesNotContain("is not a valid", ex.Message);
    }

    /// <summary>
    /// Azure spells these camelCase, and a metric written any other way would not be understood
    /// by the SDK reading the definition back.
    /// </summary>
    [Fact]
    public void Enums_AreWrittenCamelCase()
    {
        var index = new SearchIndex
        {
            Name = "vectors",
            VectorSearch = new VectorSearch
            {
                Algorithms =
                [
                    new VectorSearchAlgorithm
                    {
                        Name = "a",
                        Kind = VectorSearchAlgorithmKind.ExhaustiveKnn,
                        ExhaustiveKnnParameters = new ExhaustiveKnnParameters
                        {
                            Metric = VectorSearchMetric.DotProduct
                        }
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(index, Options);

        Assert.Contains("\"exhaustiveKnn\"", json);
        Assert.Contains("\"dotProduct\"", json);
    }
}
