using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for Edm.ComplexType and Collection(Edm.ComplexType) field support (issue #7):
/// indexing, retrieval, filtering by sub-field path, and lambda filtering over collections
/// of complex objects.
/// </summary>
/// <remarks>
/// Azure Search addresses sub-fields by a slash-delimited path (<c>Address/City</c>) and
/// requires an any/all lambda to filter a collection of complex objects — see
/// https://learn.microsoft.com/en-us/azure/search/search-howto-complex-data-types.
/// </remarks>
public class ComplexTypeTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcher;

    public ComplexTypeTests()
    {
        var index = CreateIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments(index));
        _searcher = new LuceneNetIndexSearcher(new StubReaderFactory(_helper.Directory));
    }

    public void Dispose() => _helper.Dispose();

    /// <summary>
    /// A hotel index covering the shapes that matter: a single complex field, a complex
    /// field nested inside it, a collection of complex objects, and a primitive collection
    /// nested inside that collection.
    /// </summary>
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
            new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Fields =
                {
                    new SearchField { Name = "Street", Type = "Edm.String", Searchable = true },
                    new SearchField { Name = "City", Type = "Edm.String", Searchable = true, Filterable = true, Sortable = true },
                    new SearchField { Name = "PostalCode", Type = "Edm.String", Filterable = true },
                    new SearchField
                    {
                        Name = "Geo",
                        Type = ComplexTypeSupport.ComplexType,
                        Fields =
                        {
                            new SearchField { Name = "Lat", Type = "Edm.Double", Filterable = true, Sortable = true },
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
                    new SearchField { Name = "Type", Type = "Edm.String", Searchable = true, Filterable = true },
                    new SearchField { Name = "BaseRate", Type = "Edm.Double", Filterable = true },
                    new SearchField { Name = "SmokingAllowed", Type = "Edm.Boolean", Filterable = true },
                    new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = true, Filterable = true },
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
                ["Address"] = new JsonObject
                {
                    ["Street"] = "1 Pike Place",
                    ["City"] = "Seattle",
                    ["PostalCode"] = "98101",
                    ["Geo"] = new JsonObject { ["Lat"] = 47.6062, ["Lon"] = -122.3321 },
                },
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Deluxe",
                        ["BaseRate"] = 250.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray("wifi", "view"),
                    },
                    new JsonObject
                    {
                        ["Type"] = "Standard",
                        ["BaseRate"] = 120.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray("wifi"),
                    }),
            },
            new()
            {
                ["Id"] = "2",
                ["Name"] = "Bellevue Suites",
                ["Address"] = new JsonObject
                {
                    ["Street"] = "500 Bellevue Way",
                    ["City"] = "Bellevue",
                    ["PostalCode"] = "98004",
                    ["Geo"] = new JsonObject { ["Lat"] = 47.6101, ["Lon"] = -122.2015 },
                },
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Suite",
                        ["BaseRate"] = 400.0,
                        ["SmokingAllowed"] = true,
                        ["Tags"] = new JsonArray("kitchen", "wifi"),
                    }),
            },
            new()
            {
                ["Id"] = "3",
                ["Name"] = "Portland Budget",
                ["Address"] = new JsonObject
                {
                    ["Street"] = "9 Burnside",
                    ["City"] = "Portland",
                    ["PostalCode"] = "97209",
                    ["Geo"] = new JsonObject { ["Lat"] = 45.5152, ["Lon"] = -122.6784 },
                },
                ["Rooms"] = new JsonArray(
                    new JsonObject
                    {
                        ["Type"] = "Standard",
                        ["BaseRate"] = 90.0,
                        ["SmokingAllowed"] = true,
                        ["Tags"] = new JsonArray("parking"),
                    },
                    new JsonObject
                    {
                        ["Type"] = "Standard",
                        ["BaseRate"] = 95.0,
                        ["SmokingAllowed"] = false,
                        ["Tags"] = new JsonArray(),
                    }),
            },
            // A hotel with no rooms at all, so the lambda operators have an empty collection
            // to reckon with.
            new()
            {
                ["Id"] = "4",
                ["Name"] = "Empty Inn",
                ["Address"] = new JsonObject
                {
                    ["Street"] = "0 Nowhere",
                    ["City"] = "Seattle",
                    ["PostalCode"] = "98999",
                    ["Geo"] = new JsonObject { ["Lat"] = 47.0, ["Lon"] = -122.0 },
                },
                ["Rooms"] = new JsonArray(),
            },
        };

        return rows.Select(row =>
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
        }).ToList();
    }

    private async Task<List<string>> SearchIds(string filter)
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Filter = filter,
            Top = 50
        });

        return response.Results
            .Select(r => r["Id"]!.GetValue<string>())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    // ===== Filtering on a sub-field path =====

    [Fact]
    public async Task Filter_OnSubFieldPath_MatchesByValue()
    {
        var ids = await SearchIds("Address/City eq 'Seattle'");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task Filter_OnSubFieldPath_NoMatch_ReturnsEmpty()
    {
        var ids = await SearchIds("Address/City eq 'Denver'");

        Assert.Empty(ids);
    }

    [Fact]
    public async Task Filter_OnNestedSubFieldPath_MatchesByRange()
    {
        // Only the two Puget Sound hotels sit north of latitude 47.5; Portland is at 45.5
        // and Empty Inn at exactly 47.0.
        var ids = await SearchIds("Address/Geo/Lat gt 47.5");

        Assert.Equal(["1", "2"], ids);
    }

    [Fact]
    public async Task Filter_CombiningSubFieldWithTopLevelField_AppliesBoth()
    {
        var ids = await SearchIds("Address/City eq 'Seattle' and Address/PostalCode eq '98101'");

        Assert.Equal(["1"], ids);
    }

    [Fact]
    public async Task Filter_NotEqualOnSubField_ExcludesMatchingDocs()
    {
        var ids = await SearchIds("Address/City ne 'Seattle'");

        Assert.Equal(["2", "3"], ids);
    }

    // ===== Lambdas over Collection(Edm.ComplexType) =====

    [Fact]
    public async Task Any_OnComplexCollectionSubField_MatchesWhenAnyElementQualifies()
    {
        var ids = await SearchIds("Rooms/any(r: r/Type eq 'Deluxe')");

        Assert.Equal(["1"], ids);
    }

    [Fact]
    public async Task Any_OnComplexCollectionSubField_MatchesViaNonFirstElement()
    {
        // "Standard" is hotel 1's *second* room, which catches an implementation that only
        // considers the first element of the collection.
        var ids = await SearchIds("Rooms/any(r: r/Type eq 'Standard')");

        Assert.Equal(["1", "3"], ids);
    }

    [Fact]
    public async Task Any_OnComplexCollectionNumericSubField_MatchesByRange()
    {
        // Hotel 1 has a 120.0 room and hotel 3 has 90.0/95.0 rooms; hotel 2's cheapest is 400.
        var ids = await SearchIds("Rooms/any(r: r/BaseRate lt 130)");

        Assert.Equal(["1", "3"], ids);
    }

    [Fact]
    public async Task Any_OnComplexCollectionBooleanSubField_MatchesByValue()
    {
        var ids = await SearchIds("Rooms/any(r: r/SmokingAllowed eq true)");

        Assert.Equal(["2", "3"], ids);
    }

    [Fact]
    public async Task Any_OnEmptyComplexCollection_DoesNotMatch()
    {
        // Hotel 4 has no rooms, so no predicate over its rooms can hold.
        var ids = await SearchIds("Rooms/any(r: r/BaseRate gt 0)");

        Assert.DoesNotContain("4", ids);
    }

    [Fact]
    public async Task All_OnComplexCollectionSubField_RequiresEveryElementToQualify()
    {
        // Hotel 3's rooms are 90.0 and 95.0, both under 100. Hotel 1 has a 250.0 room, so it
        // fails. Hotel 4 has no rooms, and "all" over an empty collection is vacuously true.
        var ids = await SearchIds("Rooms/all(r: r/BaseRate lt 100)");

        Assert.Equal(["3", "4"], ids);
    }

    [Fact]
    public async Task All_OnComplexCollectionSubField_ExcludesDocWithOneFailingElement()
    {
        // Hotel 2's only room is a suite; hotel 1 and hotel 3 each have a "Standard" room.
        // Hotel 4 has no rooms, so "all" holds vacuously.
        var ids = await SearchIds("Rooms/all(r: r/Type ne 'Standard')");

        Assert.Equal(["2", "4"], ids);
    }

    [Fact]
    public async Task All_WithEqualityOverACollection_IsRejected()
    {
        // "every value equals x" would require knowing whether a document holds some *other*
        // value too, which the per-value indexing model cannot answer. Azure Search likewise
        // restricts all(...) over a collection to 'ne' and the comparison operators.
        await Assert.ThrowsAsync<NotImplementedException>(() =>
            _searcher.Search(_helper.Index, new SearchRequest
            {
                Search = "*",
                Filter = "Rooms/all(r: r/SmokingAllowed eq false)",
                Top = 50
            }));
    }

    [Fact]
    public async Task Any_OnPrimitiveCollectionNestedInComplexCollection_Matches()
    {
        // Rooms/Tags is a Collection(Edm.String) inside a Collection(Edm.ComplexType), so
        // the nested lambda has to resolve "t" through the outer "r".
        var ids = await SearchIds("Rooms/any(r: r/Tags/any(t: t eq 'kitchen'))");

        Assert.Equal(["2"], ids);
    }

    [Fact]
    public async Task Any_CombinedWithTopLevelFilter_AppliesBoth()
    {
        var ids = await SearchIds("Address/City eq 'Seattle' and Rooms/any(r: r/Type eq 'Deluxe')");

        Assert.Equal(["1"], ids);
    }

    // ===== Retrieval =====

    [Fact]
    public async Task GetDoc_ReturnsComplexObjectWithOriginalShape()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "1");

        Assert.NotNull(doc);

        var address = Assert.IsType<JsonObject>(doc!["Address"]);
        Assert.Equal("1 Pike Place", address["Street"]!.GetValue<string>());
        Assert.Equal("Seattle", address["City"]!.GetValue<string>());
        Assert.Equal("98101", address["PostalCode"]!.GetValue<string>());

        var geo = Assert.IsType<JsonObject>(address["Geo"]);
        Assert.Equal(47.6062, geo["Lat"]!.GetValue<double>(), 4);
        Assert.Equal(-122.3321, geo["Lon"]!.GetValue<double>(), 4);
    }

    [Fact]
    public async Task GetDoc_ReturnsComplexCollectionPreservingElementGrouping()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "1");

        Assert.NotNull(doc);

        var rooms = Assert.IsType<JsonArray>(doc!["Rooms"]);
        Assert.Equal(2, rooms.Count);

        // The rate must stay attached to the room it belongs to: flattened leaves alone
        // could not tell 250.0 from 120.0.
        var deluxe = Assert.IsType<JsonObject>(rooms[0]);
        Assert.Equal("Deluxe", deluxe["Type"]!.GetValue<string>());
        Assert.Equal(250.0, deluxe["BaseRate"]!.GetValue<double>());

        var standard = Assert.IsType<JsonObject>(rooms[1]);
        Assert.Equal("Standard", standard["Type"]!.GetValue<string>());
        Assert.Equal(120.0, standard["BaseRate"]!.GetValue<double>());

        var tags = Assert.IsType<JsonArray>(deluxe["Tags"]);
        Assert.Equal(["wifi", "view"], tags.Select(t => t!.GetValue<string>()));
    }

    [Fact]
    public async Task GetDoc_EmptyComplexCollection_RoundTripsAsEmptyArray()
    {
        var doc = await _searcher.GetDoc(_helper.Index, "4");

        Assert.NotNull(doc);

        var rooms = Assert.IsType<JsonArray>(doc!["Rooms"]);
        Assert.Empty(rooms);
    }

    [Fact]
    public async Task GetDoc_OmitsNonRetrievableSubField()
    {
        // The stored JSON sidecar holds the whole object, so a hidden sub-field has to be
        // stripped on the way out rather than simply never being written.
        var index = new SearchIndex
        {
            Name = "hidden",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
                new SearchField
                {
                    Name = "Address",
                    Type = ComplexTypeSupport.ComplexType,
                    Fields =
                    {
                        new SearchField { Name = "City", Type = "Edm.String", Filterable = true },
                        new SearchField { Name = "Secret", Type = "Edm.String", Retrievable = false },
                    }
                },
            ]
        };

        var row = new JsonObject
        {
            ["Id"] = "1",
            ["Address"] = new JsonObject { ["City"] = "Seattle", ["Secret"] = "classified" },
        };

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

        using var helper = new LuceneTestHelper(index, [doc]);
        var searcher = new LuceneNetIndexSearcher(new StubReaderFactory(helper.Directory));

        var result = await searcher.GetDoc(index, "1");

        Assert.NotNull(result);
        var address = Assert.IsType<JsonObject>(result!["Address"]);
        Assert.Equal("Seattle", address["City"]!.GetValue<string>());
        Assert.False(address.ContainsKey("Secret"));
    }

    // ===== Full-text search over sub-fields =====

    [Fact]
    public async Task Search_MatchesTextInSearchableSubField()
    {
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "Burnside",
            Top = 50
        });

        var ids = response.Results.Select(r => r["Id"]!.GetValue<string>()).ToList();

        Assert.Equal(["3"], ids);
    }

    [Fact]
    public async Task Search_RestrictedToSubFieldPath_OnlySearchesThatField()
    {
        // "Seattle" appears both in hotel 1's Name and in two hotels' Address/City, so
        // restricting to the city sub-field must widen the result rather than narrow it to
        // the name match.
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "Seattle",
            SearchFields = "Address/City",
            Top = 50
        });

        var ids = response.Results
            .Select(r => r["Id"]!.GetValue<string>())
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["1", "4"], ids);
    }

    // ===== Sorting =====

    [Fact]
    public async Task OrderBy_SubFieldPath_SortsByThatSubField()
    {
        // Sorts on Address/Geo/Lat, a nested numeric sub-field, which also proves the sort
        // resolves a path more than one level deep.
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = "Address/Geo/Lat asc",
            Top = 50
        });

        var ids = response.Results.Select(r => r["Id"]!.GetValue<string>()).ToList();

        // Portland (45.5), Empty Inn (47.0), Seattle (47.6062), Bellevue (47.6101).
        Assert.Equal(["3", "4", "1", "2"], ids);
    }

    [Fact]
    public async Task OrderBy_FieldNamedInDifferentCase_StillSorts()
    {
        // Field names are matched case-insensitively in query expressions, but Lucene field
        // names are case-sensitive, so the schema's own spelling has to reach the SortField.
        var response = await _searcher.Search(_helper.Index, new SearchRequest
        {
            Search = "*",
            Orderby = "address/geo/lat asc",
            Top = 50
        });

        var ids = response.Results.Select(r => r["Id"]!.GetValue<string>()).ToList();

        Assert.Equal(["3", "4", "1", "2"], ids);
    }

    [Fact]
    public async Task Filter_FieldNamedInDifferentCase_StillMatches()
    {
        var ids = await SearchIds("address/city eq 'Seattle'");

        Assert.Equal(["1", "4"], ids);
    }

    [Fact]
    public async Task OrderBy_SubFieldOfComplexCollection_IsRejected()
    {
        // A sub-field under a Collection(Edm.ComplexType) has one value per element, so
        // there is no single value to sort a document by.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Search(_helper.Index, new SearchRequest
            {
                Search = "*",
                Orderby = "Rooms/BaseRate asc",
                Top = 50
            }));

        Assert.Contains("Rooms", ex.Message);
    }

    [Fact]
    public async Task OrderBy_ComplexFieldItself_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _searcher.Search(_helper.Index, new SearchRequest
            {
                Search = "*",
                Orderby = "Address asc",
                Top = 50
            }));

        Assert.Contains("Address", ex.Message);
    }

    // ===== Filterability =====

    [Fact]
    public async Task Filter_OnNonFilterableSubField_IsRejected()
    {
        var index = new SearchIndex
        {
            Name = "nonfilterable",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField
                {
                    Name = "Address",
                    Type = ComplexTypeSupport.ComplexType,
                    Fields =
                    {
                        new SearchField { Name = "Street", Type = "Edm.String", Filterable = false },
                    }
                },
            ]
        };

        using var helper = new LuceneTestHelper(index, []);
        var searcher = new LuceneNetIndexSearcher(new StubReaderFactory(helper.Directory));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            searcher.Search(index, new SearchRequest
            {
                Search = "*",
                Filter = "Address/Street eq 'x'",
                Top = 50
            }));

        Assert.Contains("Address/Street", ex.Message);
    }

    private class StubReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}

/// <summary>
/// Tests for the schema-level helpers backing complex type support, independent of Lucene.
/// </summary>
public class ComplexTypeSupportTests
{
    private static SearchIndex CreateIndex() => new()
    {
        Name = "hotels",
        Fields =
        [
            new SearchField { Name = "Id", Type = "Edm.String", Key = true },
            new SearchField
            {
                Name = "Address",
                Type = ComplexTypeSupport.ComplexType,
                Fields =
                {
                    new SearchField { Name = "City", Type = "Edm.String" },
                    new SearchField
                    {
                        Name = "Geo",
                        Type = ComplexTypeSupport.ComplexType,
                        Fields = { new SearchField { Name = "Lat", Type = "Edm.Double" } }
                    },
                }
            },
            new SearchField
            {
                Name = "Rooms",
                Type = ComplexTypeSupport.ComplexCollectionType,
                Fields = { new SearchField { Name = "BaseRate", Type = "Edm.Double" } }
            },
        ]
    };

    [Fact]
    public void EnumerateLeafFields_YieldsEveryLeafUnderItsFullPath()
    {
        var paths = ComplexTypeSupport.EnumerateLeafFields(CreateIndex())
            .Select(i => i.Path)
            .ToList();

        Assert.Equal(["Id", "Address/City", "Address/Geo/Lat", "Rooms/BaseRate"], paths);
    }

    [Theory]
    [InlineData("Address/City", "City")]
    [InlineData("Address/Geo/Lat", "Lat")]
    [InlineData("Rooms/BaseRate", "BaseRate")]
    [InlineData("Id", "Id")]
    public void FindFieldByPath_ResolvesNestedPaths(string path, string expectedName)
    {
        var field = ComplexTypeSupport.FindFieldByPath(CreateIndex(), path);

        Assert.NotNull(field);
        Assert.Equal(expectedName, field!.Name);
    }

    [Fact]
    public void FindFieldByPath_IsCaseInsensitive()
    {
        var field = ComplexTypeSupport.FindFieldByPath(CreateIndex(), "address/city");

        Assert.NotNull(field);
        Assert.Equal("City", field!.Name);
    }

    [Theory]
    [InlineData("Address/Nope")]
    [InlineData("Nope/City")]
    [InlineData("")]
    public void FindFieldByPath_ReturnsNullForUnknownPath(string path)
    {
        Assert.Null(ComplexTypeSupport.FindFieldByPath(CreateIndex(), path));
    }

    [Fact]
    public void FindComplexCollectionAncestorPath_FindsTheCollection()
    {
        Assert.Equal("Rooms", ComplexTypeSupport.FindComplexCollectionAncestorPath(CreateIndex(), "Rooms/BaseRate"));
    }

    [Theory]
    [InlineData("Address/City")]
    [InlineData("Address/Geo/Lat")]
    [InlineData("Id")]
    public void FindComplexCollectionAncestorPath_ReturnsNullOutsideACollection(string path)
    {
        Assert.Null(ComplexTypeSupport.FindComplexCollectionAncestorPath(CreateIndex(), path));
    }

    [Fact]
    public void ValidateComplexField_RejectsComplexFieldWithNoSubFields()
    {
        var field = new SearchField { Name = "Address", Type = ComplexTypeSupport.ComplexType };

        var ex = Assert.Throws<InvalidOperationException>(() => ComplexTypeSupport.ValidateComplexField(field));

        Assert.Contains("Address", ex.Message);
    }

    [Fact]
    public void ValidateComplexField_RejectsKeySubField()
    {
        var field = new SearchField
        {
            Name = "Address",
            Type = ComplexTypeSupport.ComplexType,
            Fields = { new SearchField { Name = "City", Type = "Edm.String", Key = true } }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ComplexTypeSupport.ValidateComplexField(field));

        Assert.Contains("Address/City", ex.Message);
    }

    [Fact]
    public void ValidateComplexField_AcceptsAWellFormedNestedSchema()
    {
        foreach (var field in CreateIndex().Fields)
        {
            ComplexTypeSupport.ValidateComplexField(field);
        }
    }
}
