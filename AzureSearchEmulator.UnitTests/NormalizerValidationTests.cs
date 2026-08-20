using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for the validation that refuses a normalizer definition Azure would refuse
/// (issue #74).
/// </summary>
/// <remarks>
/// Reporting these when the index is created is the point: every one of them would otherwise
/// surface far from the mistake — as a filter that silently matches nothing, or as an
/// unstructured failure against whichever document first reached the field.
/// </remarks>
public class NormalizerValidationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string? Validate(string json)
        => NormalizerValidator.FindInvalidNormalizer(
            JsonSerializer.Deserialize<SearchIndex>(json, Options)!);

    [Fact]
    public void ValidIndex_IsAccepted()
    {
        Assert.Null(Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "city", "type": "Edm.String", "filterable": true, "normalizer": "my_norm" },
                { "name": "code", "type": "Edm.String", "facetable": true, "normalizer": "lowercase" },
                { "name": "tags", "type": "Collection(Edm.String)", "filterable": true, "normalizer": "standard" }
              ],
              "normalizers": [
                { "name": "my_norm", "@odata.type": "#Microsoft.Azure.Search.CustomNormalizer",
                  "tokenFilters": ["lowercase", "asciifolding"] }
              ]
            }
            """));
    }

    /// <summary>
    /// Every predefined name is usable without being defined.
    /// </summary>
    [Theory]
    [InlineData("standard")]
    [InlineData("lowercase")]
    [InlineData("uppercase")]
    [InlineData("asciifolding")]
    [InlineData("elision")]
    public void PredefinedNormalizerName_IsAccepted(string name)
    {
        Assert.Null(Validate(
            $$"""
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "city", "type": "Edm.String", "filterable": true, "normalizer": "{{name}}" }
              ]
            }
            """));
    }

    [Fact]
    public void UnknownNormalizerName_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "city", "type": "Edm.String", "filterable": true, "normalizer": "nope" }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("'city'", error);
        Assert.Contains("'nope'", error);
    }

    /// <summary>
    /// A normalizer on a field no filter, facet or sort can reach would never apply, so naming
    /// one there is reported rather than quietly ignored.
    /// </summary>
    [Fact]
    public void NormalizerOnSearchOnlyField_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "body", "type": "Edm.String", "searchable": true,
                  "filterable": false, "sortable": false, "facetable": false,
                  "normalizer": "lowercase" }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("'body'", error);
        Assert.Contains("filterable", error);
    }

    [Fact]
    public void NormalizerOnNonStringField_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "rating", "type": "Edm.Int32", "filterable": true, "normalizer": "lowercase" }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("'rating'", error);
        Assert.Contains("Edm.String", error);
    }

    [Fact]
    public void DuplicateNormalizerName_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [ { "name": "id", "type": "Edm.String", "key": true } ],
              "normalizers": [
                { "name": "dup", "tokenFilters": ["lowercase"] },
                { "name": "DUP", "tokenFilters": ["uppercase"] }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("more than one normalizer", error);
    }

    [Fact]
    public void CustomNormalizerTakingAPredefinedName_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [ { "name": "id", "type": "Edm.String", "key": true } ],
              "normalizers": [ { "name": "lowercase", "tokenFilters": ["uppercase"] } ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("predefined normalizer", error);
    }

    [Fact]
    public void UnnamedNormalizer_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [ { "name": "id", "type": "Edm.String", "key": true } ],
              "normalizers": [ { "tokenFilters": ["lowercase"] } ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("must have a name", error);
    }

    /// <summary>
    /// A normalizer must produce one token, so the filters that would split, drop or multiply
    /// it are refused — as Azure refuses them.
    /// </summary>
    /// <remarks>
    /// <c>html_strip</c> covers the char filter side: it is legal in an analyzer and not in a
    /// normalizer, so accepting it here would be the emulator being laxer than the service.
    /// </remarks>
    [Theory]
    [InlineData("\"tokenFilters\": [\"ngram\"]")]
    [InlineData("\"tokenFilters\": [\"stopwords\"]")]
    [InlineData("\"tokenFilters\": [\"snowball\"]")]
    [InlineData("\"charFilters\": [\"html_strip\"]")]
    public void FilterNotAllowedInANormalizer_IsRejected(string chain)
    {
        var error = Validate(
            $$"""
            {
              "name": "test",
              "fields": [ { "name": "id", "type": "Edm.String", "key": true } ],
              "normalizers": [ { "name": "n", {{chain}} } ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("single token", error);
    }

    /// <summary>
    /// The check resolves a defined component to the built-in it names, so a disallowed filter
    /// cannot be smuggled in under an innocuous name.
    /// </summary>
    [Fact]
    public void DisallowedFilterUnderACustomName_IsRejected()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [ { "name": "id", "type": "Edm.String", "key": true } ],
              "normalizers": [ { "name": "n", "tokenFilters": ["innocuous"] } ],
              "tokenFilters": [
                { "name": "innocuous", "@odata.type": "#Microsoft.Azure.Search.NGramTokenFilterV2",
                  "minGram": 2, "maxGram": 3 }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("single token", error);
    }

    /// <summary>
    /// A customized version of an allowed filter is still allowed, resolved by its type rather
    /// than by the name it was given.
    /// </summary>
    [Fact]
    public void AllowedFilterUnderACustomName_IsAccepted()
    {
        Assert.Null(Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                { "name": "city", "type": "Edm.String", "filterable": true, "normalizer": "n" }
              ],
              "normalizers": [ { "name": "n", "tokenFilters": ["my_folding"] } ],
              "tokenFilters": [
                { "name": "my_folding", "@odata.type": "#Microsoft.Azure.Search.AsciiFoldingTokenFilter",
                  "preserveOriginal": false }
              ]
            }
            """));
    }

    /// <summary>
    /// A normalizer named on a sub-field of a complex type is validated too, and reported
    /// against the path that identifies it.
    /// </summary>
    [Fact]
    public void NormalizerOnComplexSubField_IsValidated()
    {
        var error = Validate(
            """
            {
              "name": "test",
              "fields": [
                { "name": "id", "type": "Edm.String", "key": true },
                {
                  "name": "address", "type": "Edm.ComplexType",
                  "fields": [
                    { "name": "city", "type": "Edm.String", "filterable": true, "normalizer": "nope" }
                  ]
                }
              ]
            }
            """);

        Assert.NotNull(error);
        Assert.Contains("address/city", error);
    }
}
