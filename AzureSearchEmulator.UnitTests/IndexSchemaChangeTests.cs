using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests that a <c>CreateOrUpdate</c> altering an existing field's immutable attributes is
/// refused, as real Azure Search refuses it (issue #32).
/// </summary>
/// <remarks>
/// The definitions here are built by deserializing JSON rather than by constructing
/// <see cref="SearchField"/> directly, because the defaults applied during deserialization are
/// half of what is under test: a caller who omits <c>filterable</c> on both sides must not be
/// read as having changed it.
/// </remarks>
public class IndexSchemaChangeTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    private static SearchIndex Index(string fieldsJson)
        => JsonSerializer.Deserialize<SearchIndex>(
               $$"""{ "name": "hotels", "fields": {{fieldsJson}} }""", Options)
           ?? throw new InvalidOperationException("Index deserialized to null");

    private const string BaseFields =
        """
        [
          { "name": "id", "type": "Edm.String", "key": true },
          { "name": "title", "type": "Edm.String", "searchable": true, "filterable": false }
        ]
        """;

    [Fact]
    public void UnchangedDefinition_IsAllowed()
    {
        Assert.Null(IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), Index(BaseFields)));
    }

    /// <summary>
    /// The scenario from the issue: a field that was created without <c>filterable</c> being
    /// toggled on.
    /// </summary>
    [Fact]
    public void TogglingFilterable_IsRefused()
    {
        var updated = Index(
            """
            [
              { "name": "id", "type": "Edm.String", "key": true },
              { "name": "title", "type": "Edm.String", "searchable": true, "filterable": true }
            ]
            """);

        Assert.Equal(
            "Existing field 'title' cannot be changed.",
            IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), updated));
    }

    [Theory]
    [InlineData("""{ "name": "title", "type": "Edm.Int32", "searchable": true, "filterable": false }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": false, "filterable": false }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "sortable": true }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "facetable": true }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "key": true }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "retrievable": false }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "analyzer": "en.lucene" }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "searchAnalyzer": "en.lucene" }""")]
    [InlineData("""{ "name": "title", "type": "Edm.String", "searchable": true, "filterable": false, "indexAnalyzer": "en.lucene" }""")]
    public void ChangingAnImmutableAttribute_IsRefused(string titleFieldJson)
    {
        var updated = Index(
            $$"""[ { "name": "id", "type": "Edm.String", "key": true }, {{titleFieldJson}} ]""");

        Assert.Equal(
            "Existing field 'title' cannot be changed.",
            IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), updated));
    }

    [Fact]
    public void RemovingAnExistingField_IsRefused()
    {
        var updated = Index("""[ { "name": "id", "type": "Edm.String", "key": true } ]""");

        Assert.Equal(
            "Existing field 'title' cannot be deleted.",
            IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), updated));
    }

    /// <summary>
    /// Adding a field is the one schema change Azure Search does allow through
    /// <c>CreateOrUpdate</c>.
    /// </summary>
    [Fact]
    public void AddingANewField_IsAllowed()
    {
        var updated = Index(
            """
            [
              { "name": "id", "type": "Edm.String", "key": true },
              { "name": "title", "type": "Edm.String", "searchable": true, "filterable": false },
              { "name": "rating", "type": "Edm.Double", "filterable": true }
            ]
            """);

        Assert.Null(IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), updated));
    }

    /// <summary>
    /// Field names are compared case-insensitively, so a caller who changes only the casing of
    /// a field name is not treated as having deleted it and added another.
    /// </summary>
    [Fact]
    public void FieldNameCasingChange_IsAllowed()
    {
        var updated = Index(
            """
            [
              { "name": "id", "type": "Edm.String", "key": true },
              { "name": "Title", "type": "Edm.String", "searchable": true, "filterable": false }
            ]
            """);

        Assert.Null(IndexSchemaChangeValidator.FindDisallowedChange(Index(BaseFields), updated));
    }

    [Fact]
    public void ChangingAComplexSubField_IsRefusedByItsPath()
    {
        var existing = Index(
            """
            [
              { "name": "id", "type": "Edm.String", "key": true },
              {
                "name": "address", "type": "Edm.ComplexType",
                "fields": [ { "name": "city", "type": "Edm.String", "searchable": true } ]
              }
            ]
            """);

        var updated = Index(
            """
            [
              { "name": "id", "type": "Edm.String", "key": true },
              {
                "name": "address", "type": "Edm.ComplexType",
                "fields": [ { "name": "city", "type": "Edm.String", "searchable": true, "facetable": true } ]
              }
            ]
            """);

        Assert.Equal(
            "Existing field 'address/city' cannot be changed.",
            IndexSchemaChangeValidator.FindDisallowedChange(existing, updated));
    }

    /// <summary>
    /// A property the emulator does not model is not part of the field's shape as far as this
    /// check is concerned, so changing one does not block the update.
    /// </summary>
    [Fact]
    public void ChangingAnUnmodelledFieldProperty_IsAllowed()
    {
        var existing = Index(
            """[ { "name": "id", "type": "Edm.String", "key": true, "synonymMaps": ["a"] } ]""");

        var updated = Index(
            """[ { "name": "id", "type": "Edm.String", "key": true, "synonymMaps": ["b"] } ]""");

        Assert.Null(IndexSchemaChangeValidator.FindDisallowedChange(existing, updated));
    }

    /// <summary>
    /// A field's normalizer is fixed once the index exists, as its analyzer is (issue #74).
    /// </summary>
    /// <remarks>
    /// The values already indexed were folded by the old normalizer. A new one would reach the
    /// query literal only, and compare it against terms it never folded, so the field would
    /// quietly stop matching rather than start behaving differently.
    /// </remarks>
    [Fact]
    public void ChangingAFieldNormalizer_IsRejected()
    {
        var existing = Index(
            """[ { "name": "id", "type": "Edm.String", "key": true, "normalizer": "lowercase" } ]""");

        var updated = Index(
            """[ { "name": "id", "type": "Edm.String", "key": true, "normalizer": "uppercase" } ]""");

        Assert.Equal(
            "Existing field 'id' cannot be changed.",
            IndexSchemaChangeValidator.FindDisallowedChange(existing, updated));
    }
}
