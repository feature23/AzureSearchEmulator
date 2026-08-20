using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for normalizers: the predefined set, custom definitions, and the validation that
/// refuses a definition Azure would refuse (issue #74).
/// </summary>
/// <remarks>
/// These cover the transformation in isolation — that a given normalizer turns a given string
/// into a given string. <see cref="NormalizerEndToEndTests"/> covers the part that matters more,
/// which is that the same transformation reaches both the indexed value and the query literal.
/// </remarks>
public class NormalizerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static SearchIndex CreateIndex(string? normalizersJson = null, string? componentsJson = null)
    {
        var json = $$"""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "city", "type": "Edm.String", "filterable": true, "facetable": true }
              ],
              {{(componentsJson == null ? "" : componentsJson + ",")}}
              "normalizers": [{{normalizersJson ?? ""}}]
            }
            """;

        return JsonSerializer.Deserialize<SearchIndex>(json, Options)!;
    }

    [Theory]
    // standard is lowercase followed by asciifolding, so it does both.
    [InlineData("standard", "Vis-à-vis MEANS Opposite", "vis-a-vis means opposite")]
    [InlineData("lowercase", "LAS VEGAS", "las vegas")]
    [InlineData("uppercase", "las vegas", "LAS VEGAS")]
    // asciifolding leaves case alone; only the accented characters change.
    [InlineData("asciifolding", "Vis-à-vis", "Vis-a-vis")]
    // elision strips the leading article, which is what it exists for in French.
    [InlineData("elision", "l'avion", "avion")]
    public void PredefinedNormalizer_TransformsAsAzureDocuments(string name, string input, string expected)
    {
        var index = CreateIndex();

        Assert.Equal(expected, NormalizerHelper.Normalize(index, name, input));
    }

    /// <summary>
    /// A normalizer emits the whole value as one token, which is the property every filter
    /// comparison depends on.
    /// </summary>
    /// <remarks>
    /// Asserted on a value with spaces and punctuation, since those are exactly what a
    /// tokenizer would split on — if anything in the chain were tokenizing, this is where it
    /// would show.
    /// </remarks>
    [Fact]
    public void Normalizer_KeepsTheWholeValueAsOneToken()
    {
        var index = CreateIndex();

        Assert.Equal("las vegas, nv 89101", NormalizerHelper.Normalize(index, "lowercase", "Las Vegas, NV 89101"));
    }

    [Fact]
    public void CustomNormalizer_AppliesCharFiltersThenTokenFilters()
    {
        // The example from Azure's own documentation: dashes to underscores and spaces removed
        // by char filters, then asciifolding, elision and lowercase applied to the token.
        var index = CreateIndex(
            """
            {
              "name": "my_custom_normalizer",
              "@odata.type": "#Microsoft.Azure.Search.CustomNormalizer",
              "charFilters": ["map_dash", "remove_whitespace"],
              "tokenFilters": ["my_asciifolding", "elision", "lowercase"]
            }
            """,
            """
            "charFilters": [
              {
                "name": "map_dash",
                "@odata.type": "#Microsoft.Azure.Search.MappingCharFilter",
                "mappings": ["-=>_"]
              },
              {
                "name": "remove_whitespace",
                "@odata.type": "#Microsoft.Azure.Search.MappingCharFilter",
                "mappings": ["\\u0020=>"]
              }
            ],
            "tokenFilters": [
              {
                "name": "my_asciifolding",
                "@odata.type": "#Microsoft.Azure.Search.AsciiFoldingTokenFilter",
                "preserveOriginal": false
              }
            ]
            """);

        Assert.Equal(
            "vis_a_vismeansopposite",
            NormalizerHelper.Normalize(index, "my_custom_normalizer", "Vis-à-vis means Opposite"));
    }

    /// <summary>
    /// Order within the chain is significant, and the emulator applies it as declared.
    /// </summary>
    /// <remarks>
    /// Mapping "ss" before folding is not the same as folding first: German normalization turns
    /// "ß" into "ss", so a mapping that runs after it sees a character the input never
    /// contained. Two chains over the same filters, differing only in order, prove the order is
    /// honoured rather than incidental.
    /// </remarks>
    [Fact]
    public void CustomNormalizer_AppliesTokenFiltersInOrder()
    {
        var index = CreateIndex(
            """
            {
              "name": "upper_then_fold",
              "charFilters": [],
              "tokenFilters": ["uppercase", "asciifolding"]
            },
            {
              "name": "fold_then_upper",
              "charFilters": [],
              "tokenFilters": ["asciifolding", "uppercase"]
            }
            """);

        // Uppercasing "é" gives "É", which asciifolding then reduces to "E".
        Assert.Equal("CAFE", NormalizerHelper.Normalize(index, "upper_then_fold", "café"));
        Assert.Equal("CAFE", NormalizerHelper.Normalize(index, "fold_then_upper", "café"));

        // The German sharp s is where the two orders genuinely diverge. Uppercase leaves "ß"
        // alone, so the asciifolding that follows expands it to a lowercase "ss" that nothing
        // then uppercases; folding first produces the "ss" while uppercase can still reach it.
        // The two spellings are what makes the order observable rather than incidental.
        Assert.Equal("STRAssE", NormalizerHelper.Normalize(index, "upper_then_fold", "Straße"));
        Assert.Equal("STRASSE", NormalizerHelper.Normalize(index, "fold_then_upper", "Straße"));
    }

    [Fact]
    public void CustomNormalizer_ResolvedBeforePredefinedNamesAreConsulted()
    {
        // Not a collision — the validator refuses those — but a name of its own, to show the
        // index's own definitions are what a field's normalizer resolves against first.
        var index = CreateIndex(
            """
            { "name": "shout", "tokenFilters": ["uppercase"] }
            """);

        Assert.Equal("QUIET", NormalizerHelper.Normalize(index, "shout", "quiet"));
    }

    [Fact]
    public void Normalizer_WithNoFilters_LeavesTheValueAlone()
    {
        var index = CreateIndex("""{ "name": "identity" }""");

        Assert.Equal("Las Vegas", NormalizerHelper.Normalize(index, "identity", "Las Vegas"));
    }

    [Fact]
    public void UnknownNormalizerName_IsRejected()
    {
        var index = CreateIndex();

        var ex = Assert.Throws<AnalyzerDefinitionException>(
            () => NormalizerHelper.Normalize(index, "nope", "value"));

        Assert.Contains("'nope'", ex.Message);
    }

    /// <summary>
    /// A field naming no normalizer is left exactly as supplied, which is Azure's default.
    /// </summary>
    [Fact]
    public void FieldWithoutNormalizer_IsNotTransformed()
    {
        var index = CreateIndex();
        var field = index.Fields.Single(i => i.Name == "city");

        Assert.Null(field.Normalizer);
        Assert.Equal("Las Vegas", NormalizerHelper.Normalize(index, field, "Las Vegas"));
    }
}
