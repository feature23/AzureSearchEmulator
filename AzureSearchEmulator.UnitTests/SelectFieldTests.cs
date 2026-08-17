using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for <c>$select</c> field projection in search results and document lookups
/// (issue #42).
/// </summary>
/// <remarks>
/// Azure Search's <c>$select</c> takes a comma-delimited list of field paths, where a path
/// may name a complex field to take it whole or reach inside one to take a single sub-field —
/// see https://learn.microsoft.com/en-us/azure/search/search-query-odata-select. Absent a
/// <c>$select</c>, every retrievable field comes back, which is what the emulator did
/// unconditionally before this.
/// </remarks>
public class SelectFieldTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public SelectFieldTests()
    {
        var index = CreateIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments(index));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    /// <summary>
    /// A hotel index spanning every retrieval path <c>ConvertSearchDoc</c> takes: primitives,
    /// a primitive collection, a geography point, a complex field with a complex field nested
    /// inside it, and a collection of complex objects.
    /// </summary>
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
            new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
            new SearchField { Name = "Rating", Type = "Edm.Int32", Filterable = true, Sortable = true },
            new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
            new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Fields =
                {
                    new SearchField { Name = "Street", Type = "Edm.String", Searchable = true },
                    new SearchField { Name = "City", Type = "Edm.String", Filterable = true },
                    new SearchField { Name = "PostalCode", Type = "Edm.String", Filterable = true },
                    new SearchField
                    {
                        Name = "Geo",
                        Type = ComplexTypeSupport.ComplexType,
                        Fields =
                        {
                            new SearchField { Name = "Lat", Type = "Edm.Double", Filterable = true },
                            new SearchField { Name = "Lon", Type = "Edm.Double", Filterable = true },
                        }
                    },
                }
            },
            new SearchField
            {
                Name = "Rooms",
                Type = ComplexTypeSupport.ComplexCollectionType,
                Fields =
                {
                    new SearchField { Name = "Type", Type = "Edm.String", Filterable = true },
                    new SearchField { Name = "BaseRate", Type = "Edm.Double", Filterable = true },
                }
            },
        ]
    };

    private static List<Lucene.Net.Documents.Document> CreateDocuments(SearchIndex index)
    {
        var rows = new List<JsonObject>
        {
            new()
            {
                ["Id"] = "1",
                ["Name"] = "Seattle Downtown",
                ["Rating"] = 5,
                ["Tags"] = new JsonArray("wifi", "pool"),
                ["Location"] = GeoSupport.CreateGeoJsonPoint(-122.3321, 47.6062),
                ["Address"] = new JsonObject
                {
                    ["Street"] = "1 Pike Place",
                    ["City"] = "Seattle",
                    ["PostalCode"] = "98101",
                    ["Geo"] = new JsonObject { ["Lat"] = 47.6062, ["Lon"] = -122.3321 },
                },
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Deluxe", ["BaseRate"] = 250.0 },
                    new JsonObject { ["Type"] = "Standard", ["BaseRate"] = 120.0 }),
            },
            new()
            {
                ["Id"] = "2",
                ["Name"] = "Bellevue Suites",
                ["Rating"] = 4,
                ["Tags"] = new JsonArray("kitchen"),
                ["Location"] = GeoSupport.CreateGeoJsonPoint(-122.2015, 47.6101),
                ["Address"] = new JsonObject
                {
                    ["Street"] = "500 Bellevue Way",
                    ["City"] = "Bellevue",
                    ["PostalCode"] = "98004",
                    ["Geo"] = new JsonObject { ["Lat"] = 47.6101, ["Lon"] = -122.2015 },
                },
                ["Rooms"] = new JsonArray(
                    new JsonObject { ["Type"] = "Suite", ["BaseRate"] = 400.0 }),
            },
        };

        return rows.Select(row => BuildDocument(index, row)).ToList();
    }

    private static Lucene.Net.Documents.Document BuildDocument(SearchIndex index, JsonObject row)
    {
        var doc = new Lucene.Net.Documents.Document();

        foreach (var field in index.Fields)
        {
            if (row[field.Name] is { } value)
            {
                foreach (var f in field.CreateFields(value))
                {
                    doc.Add(f);
                }
            }
        }

        return doc;
    }

    private async Task<JsonObject> SearchFirst(string? select)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = "Id eq '1'",
            Select = select,
            Top = 50
        });

        return Assert.Single(response.Results);
    }

    /// <summary>
    /// The names of a result's actual fields, with the <c>@search.*</c> metadata excluded:
    /// that is added after projection and is never part of what <c>$select</c> controls.
    /// </summary>
    private static IEnumerable<string> FieldNames(JsonObject result)
        => result.Select(p => p.Key).Where(k => !k.StartsWith('@')).Order();

    // ===== Baseline: no $select =====

    [Fact]
    public async Task Search_WithoutSelect_ReturnsAllRetrievableFields()
    {
        var result = await SearchFirst(null);

        Assert.Equal(
            ["Address", "Id", "Location", "Name", "Rating", "Rooms", "Tags"],
            FieldNames(result));
    }

    [Fact]
    public async Task Search_WithWildcardSelect_ReturnsAllRetrievableFields()
    {
        // Azure Search accepts "*" in place of a field list.
        var result = await SearchFirst("*");

        Assert.Equal(
            ["Address", "Id", "Location", "Name", "Rating", "Rooms", "Tags"],
            FieldNames(result));
    }

    [Fact]
    public async Task Search_WithEmptySelect_ReturnsAllRetrievableFields()
    {
        var result = await SearchFirst("");

        Assert.Equal(
            ["Address", "Id", "Location", "Name", "Rating", "Rooms", "Tags"],
            FieldNames(result));
    }

    // ===== Top-level fields =====

    [Fact]
    public async Task Search_SelectingSingleField_ReturnsOnlyThatField()
    {
        var result = await SearchFirst("Name");

        Assert.Equal(["Name"], FieldNames(result));
        Assert.Equal("Seattle Downtown", result["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_SelectingSeveralFields_ReturnsExactlyThose()
    {
        var result = await SearchFirst("Id,Rating");

        Assert.Equal(["Id", "Rating"], FieldNames(result));
        Assert.Equal("1", result["Id"]!.GetValue<string>());
        Assert.Equal(5, result["Rating"]!.GetValue<int>());
    }

    [Fact]
    public async Task Search_SelectingWithSpacesAroundCommas_IgnoresWhitespace()
    {
        var result = await SearchFirst(" Id , Rating ");

        Assert.Equal(["Id", "Rating"], FieldNames(result));
    }

    [Fact]
    public async Task Search_SelectingFieldInDifferentCasing_MatchesSchemaField()
    {
        // Field names resolve case-insensitively, but the response uses the schema's casing.
        var result = await SearchFirst("nAmE");

        Assert.Equal(["Name"], FieldNames(result));
    }

    [Fact]
    public async Task Search_DoesNotSelectKeyFieldImplicitly()
    {
        // Azure Search returns only what was asked for; the key is not added back in.
        var result = await SearchFirst("Name");

        Assert.False(result.ContainsKey("Id"));
    }

    [Fact]
    public async Task Search_SelectingCollectionField_ReturnsWholeCollection()
    {
        var result = await SearchFirst("Tags");

        Assert.Equal(["Tags"], FieldNames(result));
        var tags = Assert.IsType<JsonArray>(result["Tags"]);
        Assert.Equal(["wifi", "pool"], tags.Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task Search_SelectingGeographyPoint_ReturnsThePoint()
    {
        // Points are stored as a separate lat/lon pair rather than under the field's own
        // name, so projection has to key off the schema field, not the Lucene field.
        var result = await SearchFirst("Location");

        Assert.Equal(["Location"], FieldNames(result));
        var location = Assert.IsType<JsonObject>(result["Location"]);
        var coords = Assert.IsType<JsonArray>(location["coordinates"]);
        Assert.Equal(-122.3321, coords[0]!.GetValue<double>(), 4);
        Assert.Equal(47.6062, coords[1]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Search_ScoreMetadataSurvivesProjection()
    {
        // @search.score is not a field and is never filtered out by $select.
        var result = await SearchFirst("Name");

        Assert.True(result.ContainsKey("@search.score"));
    }

    // ===== Complex fields =====

    [Fact]
    public async Task Search_SelectingComplexFieldByName_ReturnsWholeObject()
    {
        var result = await SearchFirst("Address");

        Assert.Equal(["Address"], FieldNames(result));

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["City", "Geo", "PostalCode", "Street"], address.Select(p => p.Key).Order());

        // Nesting is preserved all the way down when the parent is taken whole.
        var geo = Assert.IsType<JsonObject>(address["Geo"]);
        Assert.Equal(47.6062, geo["Lat"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Search_SelectingComplexSubField_ReturnsOnlyThatSubField()
    {
        var result = await SearchFirst("Address/City");

        Assert.Equal(["Address"], FieldNames(result));

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["City"], address.Select(p => p.Key));
        Assert.Equal("Seattle", address["City"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_SelectingSeveralSubFieldsOfSameComplexField_MergesThem()
    {
        var result = await SearchFirst("Address/City,Address/PostalCode");

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["City", "PostalCode"], address.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Search_SelectingNestedComplexSubField_ReturnsOnlyThatLeaf()
    {
        // Address/Geo/Lat is two levels deep, so this fails unless the selection narrows at
        // every level rather than only the first.
        var result = await SearchFirst("Address/Geo/Lat");

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["Geo"], address.Select(p => p.Key));

        var geo = Assert.IsType<JsonObject>(address["Geo"]);
        Assert.Equal(["Lat"], geo.Select(p => p.Key));
        Assert.Equal(47.6062, geo["Lat"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task Search_SelectingComplexFieldAndItsSubField_ReturnsWholeObject()
    {
        // The broader path wins: "Address" already covers everything "Address/City" asks for.
        var result = await SearchFirst("Address,Address/City");

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["City", "Geo", "PostalCode", "Street"], address.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Search_SelectingSubFieldThenParent_ReturnsWholeObject()
    {
        // Order must not matter: the narrower path arriving first is still subsumed.
        var result = await SearchFirst("Address/City,Address");

        var address = Assert.IsType<JsonObject>(result["Address"]);
        Assert.Equal(["City", "Geo", "PostalCode", "Street"], address.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Search_SelectingSubFieldOfComplexCollection_NarrowsEveryElement()
    {
        // $select addresses by path and cannot single out an element, so the same narrowing
        // applies to each — and the elements stay separate.
        var result = await SearchFirst("Rooms/Type");

        var rooms = Assert.IsType<JsonArray>(result["Rooms"]);
        Assert.Equal(2, rooms.Count);

        Assert.Equal(["Type"], Assert.IsType<JsonObject>(rooms[0]).Select(p => p.Key));
        Assert.Equal("Deluxe", rooms[0]!["Type"]!.GetValue<string>());
        Assert.Equal("Standard", rooms[1]!["Type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_SelectingComplexCollectionByName_ReturnsWholeElements()
    {
        var result = await SearchFirst("Rooms");

        var rooms = Assert.IsType<JsonArray>(result["Rooms"]);
        var first = Assert.IsType<JsonObject>(rooms[0]);
        Assert.Equal(["BaseRate", "Type"], first.Select(p => p.Key).Order());
    }

    [Fact]
    public async Task Search_MixingTopLevelAndSubFieldPaths_ReturnsBoth()
    {
        var result = await SearchFirst("Id,Address/City,Rooms/BaseRate");

        Assert.Equal(["Address", "Id", "Rooms"], FieldNames(result));
        Assert.Equal(["City"], Assert.IsType<JsonObject>(result["Address"]).Select(p => p.Key));

        var rooms = Assert.IsType<JsonArray>(result["Rooms"]);
        Assert.Equal(["BaseRate"], Assert.IsType<JsonObject>(rooms[0]).Select(p => p.Key));
    }

    // ===== Interaction with retrievability =====

    [Fact]
    public async Task Search_SelectingNonRetrievableField_StillOmitsIt()
    {
        // Retrievability wins over $select: asking for a hidden field does not reveal it.
        var index = new SearchIndex
        {
            Name = "hidden",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true, Searchable = true },
                new SearchField { Name = "Secret", Type = "Edm.String", Retrievable = false },
                new SearchField
                {
                    Name = "Address",
                    Type = ComplexTypeSupport.ComplexType,
                    Fields =
                    {
                        new SearchField { Name = "City", Type = "Edm.String", Filterable = true },
                        new SearchField { Name = "HiddenCity", Type = "Edm.String", Retrievable = false },
                    }
                },
            ]
        };

        var row = new JsonObject
        {
            ["Id"] = "1",
            ["Secret"] = "classified",
            ["Address"] = new JsonObject { ["City"] = "Seattle", ["HiddenCity"] = "classified" },
        };

        using var helper = new LuceneTestHelper(index, [BuildDocument(index, row)]);
        var searcher = new LuceneNetIndexSearcher(new StubReaderFactory(helper.Directory));

        var response = await searcher.Search(index, new SearchRequest
        {
            Search = "*",
            Select = "Id,Secret,Address/City,Address/HiddenCity",
            Top = 50
        });

        var result = Assert.Single(response.Results);

        Assert.Equal(["Address", "Id"], FieldNames(result));
        Assert.Equal(["City"], Assert.IsType<JsonObject>(result["Address"]).Select(p => p.Key));
    }

    // ===== Unknown fields =====

    [Fact]
    public async Task Search_SelectingUnknownField_Throws()
    {
        // Azure Search rejects an unknown field path rather than ignoring it, which would
        // otherwise hide a typo behind a silently narrower response.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => SearchFirst("Nonexistent"));

        Assert.Contains("Nonexistent", ex.Message);
    }

    [Fact]
    public async Task Search_SelectingUnknownSubField_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SearchFirst("Address/Nonexistent"));
    }

    // ===== Document lookup =====

    [Fact]
    public async Task GetDoc_WithoutSelect_ReturnsAllRetrievableFields()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "1");

        Assert.NotNull(doc);
        Assert.Equal(
            ["Address", "Id", "Location", "Name", "Rating", "Rooms", "Tags"],
            FieldNames(doc!));
    }

    [Fact]
    public async Task GetDoc_WithSelect_ReturnsOnlySelectedFields()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "1", "Name,Address/City");

        Assert.NotNull(doc);
        Assert.Equal(["Address", "Name"], FieldNames(doc!));
        Assert.Equal(["City"], Assert.IsType<JsonObject>(doc!["Address"]).Select(p => p.Key));
    }

    // ===== Projection applies to every result, not just the first =====

    [Fact]
    public async Task Search_ProjectionAppliesToAllResults()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Select = "Id",
            Top = 50
        });

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r => Assert.Equal(["Id"], FieldNames(r)));
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}

/// <summary>
/// Tests for the <c>$select</c> parser itself, independent of Lucene.
/// </summary>
public class FieldSelectionTests
{
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true },
            new SearchField { Name = "Name", Type = "Edm.String" },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Fields =
                {
                    new SearchField { Name = "City", Type = "Edm.String" },
                    new SearchField { Name = "Street", Type = "Edm.String" },
                }
            },
        ]
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("Name,*")]
    public void Parse_MeaningEverything_ReturnsNull(string? select)
    {
        // Null stands for "no narrowing", which is what lets retrieval skip the check
        // entirely rather than building a tree covering every field.
        Assert.Null(FieldSelection.Parse(CreateIndex(), select));
    }

    [Fact]
    public void Parse_TopLevelField_IncludesOnlyThatField()
    {
        var selection = FieldSelection.Parse(CreateIndex(), "Name");

        Assert.NotNull(selection);
        Assert.True(selection!.Includes("Name"));
        Assert.False(selection.Includes("Id"));
        Assert.False(selection.Includes("Address"));
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var selection = FieldSelection.Parse(CreateIndex(), "nAmE");

        Assert.True(selection!.Includes("Name"));
    }

    [Fact]
    public void Parse_SubFieldPath_IncludesParentAndNarrowsWithin()
    {
        var selection = FieldSelection.Parse(CreateIndex(), "Address/City");

        Assert.True(selection!.Includes("Address"));

        var sub = selection.GetSubSelection("Address");
        Assert.NotNull(sub);
        Assert.True(sub!.Includes("City"));
        Assert.False(sub.Includes("Street"));
    }

    [Fact]
    public void Parse_WholeComplexField_HasNoSubSelection()
    {
        // A null sub-selection is what tells retrieval to stop narrowing and take the
        // object whole.
        var selection = FieldSelection.Parse(CreateIndex(), "Address");

        Assert.True(selection!.Includes("Address"));
        Assert.Null(selection.GetSubSelection("Address"));
    }

    [Fact]
    public void Parse_ParentAfterChild_SubsumesTheChild()
    {
        var selection = FieldSelection.Parse(CreateIndex(), "Address/City,Address");

        Assert.Null(selection!.GetSubSelection("Address"));
    }

    [Fact]
    public void Parse_UnknownPath_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FieldSelection.Parse(CreateIndex(), "Nope"));

        Assert.Contains("Nope", ex.Message);
        Assert.Contains("hotels", ex.Message);
    }

    [Fact]
    public void Parse_UnknownSubFieldPath_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => FieldSelection.Parse(CreateIndex(), "Address/Nope"));
    }

    [Fact]
    public void Parse_PathThroughNonComplexField_Throws()
    {
        // "Name" has no sub-fields, so nothing can sit beneath it.
        Assert.Throws<InvalidOperationException>(
            () => FieldSelection.Parse(CreateIndex(), "Name/Something"));
    }
}
