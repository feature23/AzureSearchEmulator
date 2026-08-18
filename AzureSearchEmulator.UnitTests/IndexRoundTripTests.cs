using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests that index properties the emulator does not model survive a deserialize/serialize
/// round-trip (issue #41).
/// </summary>
/// <remarks>
/// The emulator stores an index definition by writing back whatever it deserialized, so a
/// property it fails to capture is not merely ignored — it is erased from the caller's own
/// definition on the next PUT. These tests assert the properties come back, and come back
/// with their structure intact rather than flattened to a string, since a client re-reading
/// the definition has to be able to use it.
/// </remarks>
public class IndexRoundTripTests
{
    /// <summary>
    /// Matches the options the app and the file repository both serialize with, so what these
    /// tests exercise is what actually reaches disk.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// An index definition using the unmodelled properties the issue named, each with enough
    /// internal structure that a lossy round-trip would be visible.
    /// </summary>
    private const string FullIndexJson =
        """
        {
          "name": "hotels",
          "fields": [
            { "name": "id", "type": "Edm.String", "key": true },
            { "name": "description", "type": "Edm.String", "searchable": true }
          ],
          "scoringProfiles": [
            {
              "name": "boostDescription",
              "text": { "weights": { "description": 2.5 } }
            }
          ],
          "corsOptions": { "allowedOrigins": ["*"], "maxAgeInSeconds": 300 },
          "analyzers": [
            { "name": "custom", "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer", "tokenizer": "standard_v2" }
          ],
          "similarity": { "@odata.type": "#Microsoft.Azure.Search.BM25Similarity", "k1": 1.2, "b": 0.75 },
          "semantic": { "configurations": [{ "name": "sem", "prioritizedFields": {} }] },
          "vectorSearch": {
            "algorithms": [{ "name": "hnswAlgo", "kind": "hnsw" }],
            "profiles": [{ "name": "vp", "algorithm": "hnswAlgo" }]
          },
          "encryptionKey": { "keyVaultKeyName": "k", "keyVaultUri": "https://example.vault.azure.net" }
        }
        """;

    private static SearchIndex Deserialize(string json)
        => JsonSerializer.Deserialize<SearchIndex>(json, Options)
           ?? throw new InvalidOperationException("Index deserialized to null");

    /// <summary>
    /// Round-trips through the same path the repository takes: deserialize, then serialize
    /// back out, and re-parse the result for inspection.
    /// </summary>
    private static JsonObject RoundTrip(string json)
    {
        var index = Deserialize(json);
        var serialized = JsonSerializer.Serialize(index, Options);
        return JsonNode.Parse(serialized)?.AsObject()
               ?? throw new InvalidOperationException("Round-tripped index is not a JSON object");
    }

    [Fact]
    public void ModelledProperties_SurviveRoundTrip()
    {
        var index = Deserialize(FullIndexJson);

        Assert.Equal("hotels", index.Name);
        Assert.Equal(["id", "description"], index.Fields.Select(i => i.Name));
        Assert.True(index.Fields[0].Key);
        Assert.True(index.Fields[1].Searchable);

        // Modelled since issue #47, so it deserializes into the property rather than the
        // extension bag.
        var profile = Assert.Single(index.ScoringProfiles);
        Assert.Equal("boostDescription", profile.Name);
        Assert.Equal(2.5, profile.Text?.Weights["description"]);

        // Modelled since issue #46, for the same reason.
        var vectorProfile = Assert.Single(index.VectorSearch?.Profiles!);
        Assert.Equal("vp", vectorProfile.Name);
        Assert.Equal("hnswAlgo", vectorProfile.Algorithm);
    }

    [Theory]
    // scoringProfiles and vectorSearch are deliberately absent: they became modelled properties
    // in issues #47 and #46, so they no longer travel through the extension bag.
    // ModelledProperties_SurviveRoundTrip and VectorSearchJsonTests cover them instead.
    [InlineData("corsOptions")]
    [InlineData("analyzers")]
    [InlineData("similarity")]
    [InlineData("semantic")]
    [InlineData("encryptionKey")]
    public void UnmodelledProperty_SurvivesRoundTrip(string property)
    {
        var result = RoundTrip(FullIndexJson);

        Assert.True(result.ContainsKey(property), $"'{property}' was dropped on round-trip");
    }

    [Fact]
    public void UnmodelledProperty_KeepsItsStructure()
    {
        var result = RoundTrip(FullIndexJson);

        // Not just present but usable: the nested shape and the numeric types have to survive,
        // otherwise a client re-reading its own definition finds it unusable.
        var origins = result["corsOptions"]?["allowedOrigins"]?.AsArray();
        Assert.Equal("*", origins?[0]?.GetValue<string>());
        Assert.Equal(300, result["corsOptions"]?["maxAgeInSeconds"]?.GetValue<int>());
    }

    /// <summary>
    /// OData type discriminators carry the meaning of a polymorphic definition — an analyzer
    /// without its <c>@odata.type</c> is ambiguous — so they have to survive the property-name
    /// handling rather than being reshaped by the camel-case policy.
    /// </summary>
    [Fact]
    public void ODataTypeAnnotation_SurvivesRoundTrip()
    {
        var result = RoundTrip(FullIndexJson);

        Assert.Equal("#Microsoft.Azure.Search.BM25Similarity",
            result["similarity"]?["@odata.type"]?.GetValue<string>());
        Assert.Equal("#Microsoft.Azure.Search.CustomAnalyzer",
            result["analyzers"]?[0]?["@odata.type"]?.GetValue<string>());
    }

    /// <summary>
    /// <c>dimensions</c> and <c>vectorSearchProfile</c> are deliberately not used here: they
    /// became modelled properties in issue #46, and <c>VectorSearchJsonTests</c> covers them.
    /// Exercising the field-level extension bag needs a property that is still unmodelled.
    /// </summary>
    [Fact]
    public void UnmodelledFieldProperty_SurvivesRoundTrip()
    {
        const string json =
            """
            {
              "name": "hotels",
              "fields": [
                {
                  "name": "city",
                  "type": "Edm.String",
                  "normalizer": "lowercase"
                }
              ]
            }
            """;

        var result = RoundTrip(json);
        var field = result["fields"]?[0];

        Assert.Equal("lowercase", field?["normalizer"]?.GetValue<string>());
    }

    /// <summary>
    /// Sub-fields of a complex field go through the same deserializer, so an unmodelled
    /// property one level down must be retained too.
    /// </summary>
    [Fact]
    public void UnmodelledSubFieldProperty_SurvivesRoundTrip()
    {
        const string json =
            """
            {
              "name": "hotels",
              "fields": [
                {
                  "name": "address",
                  "type": "Edm.ComplexType",
                  "fields": [
                    { "name": "city", "type": "Edm.String", "normalizer": "lowercase" }
                  ]
                }
              ]
            }
            """;

        var result = RoundTrip(json);

        Assert.Equal("lowercase",
            result["fields"]?[0]?["fields"]?[0]?["normalizer"]?.GetValue<string>());
    }

    /// <summary>
    /// The get-modify-put cycle from the issue, end to end: read a definition, change a
    /// modelled part of it, write it back, and confirm the unmodelled parts are still there.
    /// </summary>
    [Fact]
    public void GetModifyPut_PreservesUnmodelledProperties()
    {
        var index = Deserialize(FullIndexJson);

        index.Fields.Add(new SearchField { Name = "rating", Type = "Edm.Double", Filterable = true });

        var result = JsonNode.Parse(JsonSerializer.Serialize(index, Options))!.AsObject();

        Assert.Equal(3, result["fields"]?.AsArray().Count);
        Assert.True(result.ContainsKey("scoringProfiles"));
        Assert.True(result.ContainsKey("vectorSearch"));
        Assert.True(result.ContainsKey("semantic"));
    }

    /// <summary>
    /// The <c>[Required]</c> attributes are the validation the controller was silently
    /// skipping, so they are asserted here at the model level; the integration tests cover
    /// that binding now actually runs them.
    /// </summary>
    [Theory]
    [InlineData("""{ "type": "Edm.String", "key": true }""", nameof(SearchField.Name))]
    [InlineData("""{ "name": "id", "key": true }""", nameof(SearchField.Type))]
    public void FieldMissingRequiredProperty_FailsValidation(string fieldJson, string expected)
    {
        var field = JsonSerializer.Deserialize<SearchField>(fieldJson, Options)!;

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            field, new ValidationContext(field), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, i => i.MemberNames.Contains(expected));
    }

    /// <summary>
    /// An index using none of these properties must not gain an empty placeholder for them —
    /// the stored definition should still look like what the caller sent.
    /// </summary>
    [Fact]
    public void PlainIndex_GainsNoExtraProperties()
    {
        const string json =
            """
            { "name": "simple", "fields": [{ "name": "id", "type": "Edm.String", "key": true }] }
            """;

        var result = RoundTrip(json);

        Assert.False(result.ContainsKey("additionalProperties"));
        Assert.DoesNotContain(result, i => i.Key.Contains("dditional"));
    }
}
