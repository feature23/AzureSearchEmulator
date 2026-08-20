using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using Xunit;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers what the emulator refuses to store or to search with (issue #69).
/// </summary>
/// <remarks>
/// Each rejection here is one a caller would otherwise discover as a search that quietly
/// returned the wrong results: a map naming an unsupported format, or a field naming a map that
/// does not exist or that no query could ever reach. Reporting them when the definition is
/// written points at the mistake instead.
/// </remarks>
public class SynonymMapValidationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static SearchIndex CreateIndex(string fieldsJson) =>
        JsonSerializer.Deserialize<SearchIndex>(
            $$"""{ "name": "test", "fields": {{fieldsJson}} }""", Options)!;

    private static IReadOnlySet<string> Existing(params string[] names)
        => names.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ValidMap_IsAccepted()
    {
        var map = new SynonymMap { Name = "places", Synonyms = "usa, united states" };

        Assert.Null(SynonymMapValidator.FindInvalidSynonymMap(map));
    }

    [Fact]
    public void MapWithoutName_IsRejected()
    {
        var map = new SynonymMap { Name = "", Synonyms = "usa, united states" };

        Assert.Contains("must have a name", SynonymMapValidator.FindInvalidSynonymMap(map));
    }

    /// <summary>
    /// Azure supports only the Solr format, and a map claiming another would be stored and then
    /// expand nothing.
    /// </summary>
    [Fact]
    public void MapWithUnsupportedFormat_IsRejected()
    {
        var map = new SynonymMap { Name = "places", Format = "wordnet", Synonyms = "usa, us" };

        var error = SynonymMapValidator.FindInvalidSynonymMap(map);

        Assert.Contains("wordnet", error);
        Assert.Contains("solr", error);
    }

    /// <summary>
    /// An empty map would look to a caller as though it had been applied while doing nothing.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MapWithoutRules_IsRejected(string synonyms)
    {
        var map = new SynonymMap { Name = "places", Synonyms = synonyms };

        Assert.Contains("at least one synonym rule", SynonymMapValidator.FindInvalidSynonymMap(map));
    }

    /// <summary>
    /// The format check is case-insensitive, so a map written as "Solr" is still usable.
    /// </summary>
    [Fact]
    public void FormatComparison_IgnoresCase()
    {
        var map = new SynonymMap { Name = "places", Format = "SOLR", Synonyms = "usa, us" };

        Assert.Null(SynonymMapValidator.FindInvalidSynonymMap(map));
    }

    [Fact]
    public void FieldNamingAnExistingMap_IsAccepted()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "city", "type": "Edm.String", "searchable": true, "synonymMaps": ["places"] } ]
            """);

        Assert.Null(SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    [Fact]
    public void FieldNamingAMissingMap_IsRejected()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "city", "type": "Edm.String", "searchable": true, "synonymMaps": ["nope"] } ]
            """);

        var error = SynonymMapValidator.FindInvalidFieldReference(index, Existing("places"));

        Assert.Contains("'nope'", error);
        Assert.Contains("does not exist", error);
    }

    /// <summary>
    /// Synonyms only ever widen a full-text query, so a field no query can reach would name a
    /// map that never applied.
    /// </summary>
    [Fact]
    public void NonSearchableField_IsRejected()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "city", "type": "Edm.String", "searchable": false, "synonymMaps": ["places"] } ]
            """);

        Assert.Contains("not searchable", SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    /// <summary>
    /// A synonym map transforms text, so it is meaningless on a field that holds none.
    /// </summary>
    [Fact]
    public void NonStringField_IsRejected()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "rating", "type": "Edm.Int32", "searchable": true, "synonymMaps": ["places"] } ]
            """);

        Assert.Contains("cannot have a synonym map",
            SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    /// <summary>
    /// A string collection is still text, so it may carry a map.
    /// </summary>
    [Fact]
    public void StringCollectionField_IsAccepted()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "tags", "type": "Collection(Edm.String)", "searchable": true, "synonymMaps": ["places"] } ]
            """);

        Assert.Null(SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    /// <summary>
    /// A mistake on a sub-field is reported against the path that identifies it, rather than
    /// against the complex field that contains it.
    /// </summary>
    [Fact]
    public void SubFieldOfComplexType_IsReportedByPath()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "address", "type": "Edm.ComplexType", "fields": [
            { "name": "city", "type": "Edm.String", "searchable": true, "synonymMaps": ["nope"] } ] } ]
            """);

        Assert.Contains("address/city", SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    /// <summary>
    /// Map names are matched case-insensitively, as Azure matches them.
    /// </summary>
    [Fact]
    public void MapNameComparison_IgnoresCase()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "city", "type": "Edm.String", "searchable": true, "synonymMaps": ["PLACES"] } ]
            """);

        Assert.Null(SynonymMapValidator.FindInvalidFieldReference(index, Existing("places")));
    }

    /// <summary>
    /// A field naming no map is unaffected, whatever maps the service holds.
    /// </summary>
    [Fact]
    public void FieldNamingNoMap_IsAccepted()
    {
        var index = CreateIndex(
            """
            [ { "name": "id", "type": "Edm.String", "key": true },
            { "name": "city", "type": "Edm.String", "searchable": true } ]
            """);

        Assert.Null(SynonymMapValidator.FindInvalidFieldReference(index, Existing()));
    }
}
