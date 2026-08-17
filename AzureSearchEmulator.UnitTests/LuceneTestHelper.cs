using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Provides helpers for creating in-memory Lucene indexes for unit testing.
/// </summary>
public sealed class LuceneTestHelper : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    public RAMDirectory Directory { get; }
    public SearchIndex Index { get; }

    public LuceneTestHelper(SearchIndex index, IEnumerable<Document> documents)
    {
        Index = index;
        Directory = new RAMDirectory();

        var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index.Fields);
        var config = new IndexWriterConfig(Version, analyzer);

        using var writer = new IndexWriter(Directory, config);
        foreach (var doc in documents)
        {
            writer.AddDocument(doc);
        }
        writer.Commit();
    }

    public IndexSearcher CreateSearcher()
    {
        var reader = DirectoryReader.Open(Directory);
        return new IndexSearcher(reader);
    }

    public void Dispose()
    {
        Directory.Dispose();
    }

    /// <summary>
    /// Creates a standard product index definition used across tests.
    /// </summary>
    public static SearchIndex CreateProductIndex()
    {
        return new SearchIndex
        {
            Name = "products",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Description", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Price", Type = "Edm.Double", Searchable = false, Filterable = true, Sortable = true },
                new SearchField { Name = "Category", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "InStock", Type = "Edm.Boolean", Searchable = false, Filterable = true },
                new SearchField { Name = "Rating", Type = "Edm.Int32", Searchable = false, Filterable = true, Sortable = true },
            ]
        };
    }

    /// <summary>
    /// The product index with a suggester over Name and Description, used by the suggest and
    /// autocomplete tests (issue #45).
    /// </summary>
    public static SearchIndex CreateSuggesterProductIndex()
    {
        var index = CreateProductIndex();

        index.Suggesters.Add(new SearchSuggester
        {
            Name = "sg",
            SourceFields = ["Name", "Description"],
        });

        return index;
    }

    /// <summary>
    /// Creates standard product documents for testing.
    /// </summary>
    public static List<Document> CreateProductDocuments()
    {
        return
        [
            CreateProductDoc("1", "Laptop Pro 15", "High-performance laptop with 16GB RAM and 512GB SSD", 1299.99, "Electronics", true, 5),
            CreateProductDoc("2", "Laptop Budget 13", "Affordable laptop perfect for students and everyday use", 499.99, "Electronics", true, 4),
            CreateProductDoc("3", "Gaming Mouse", "Precision gaming mouse with 16000 DPI sensor", 59.99, "Accessories", true, 4),
            CreateProductDoc("4", "Mechanical Keyboard", "Mechanical keyboard with Cherry MX switches and RGB lighting", 149.99, "Accessories", false, 5),
            CreateProductDoc("5", "Monitor 4K", "27-inch 4K monitor with 60Hz refresh rate", 599.99, "Electronics", true, 3),
        ];
    }

    /// <summary>
    /// Creates an index used by the filter-gap tests (issue #44), whose documents leave
    /// fields unpopulated so that null comparisons have something to find.
    /// </summary>
    /// <remarks>
    /// This deliberately covers one field of each shape a presence test has to handle
    /// differently: a plain string, a searchable string (which is indexed twice, analyzed and
    /// raw), a numeric, a geography point (indexed only under its coordinate sidecars), a
    /// collection, and a complex type (which indexes nothing under its own name at all).
    /// </remarks>
    public static SearchIndex CreateNullableIndex()
    {
        return new SearchIndex
        {
            Name = "nullable",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true, Filterable = true, Sortable = true },
                new SearchField { Name = "Category", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Rating", Type = "Edm.Int32", Searchable = false, Filterable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Searchable = false, Filterable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Searchable = false, Filterable = true },
                new SearchField
                {
                    Name = "Address",
                    Type = "Edm.ComplexType",
                    Fields =
                    [
                        new SearchField { Name = "City", Type = "Edm.String", Searchable = false, Filterable = true },
                        new SearchField { Name = "PostalCode", Type = "Edm.String", Searchable = false, Filterable = true },
                    ]
                },
            ]
        };
    }

    /// <summary>
    /// Documents for <see cref="CreateNullableIndex"/>, built through the real indexing path
    /// so that "null" means exactly what the indexer writes for an absent JSON property.
    /// </summary>
    public static List<Document> CreateNullableDocuments()
    {
        return
        [
            // Every field populated.
            CreateNullableDoc("""
                {
                  "Id": "1", "Name": "Alpha", "Category": "Electronics", "Rating": 5,
                  "Location": { "type": "Point", "coordinates": [-122.3321, 47.6062] },
                  "Tags": ["red", "blue"],
                  "Address": { "City": "Seattle", "PostalCode": "98101" }
                }
                """),
            // Category and Rating absent.
            CreateNullableDoc("""
                {
                  "Id": "2", "Name": "Bravo",
                  "Location": { "type": "Point", "coordinates": [-122.2015, 47.6101] },
                  "Tags": ["green"],
                  "Address": { "City": "Bellevue", "PostalCode": "98004" }
                }
                """),
            // Explicit JSON nulls, which the indexer drops exactly as it drops absent keys.
            CreateNullableDoc("""
                {
                  "Id": "3", "Name": "Charlie", "Category": null, "Rating": null,
                  "Location": null, "Tags": null, "Address": null
                }
                """),
            // Location and Tags absent, the rest present.
            CreateNullableDoc("""
                {
                  "Id": "4", "Name": "delta", "Category": "Accessories", "Rating": 3,
                  "Address": { "City": "Tacoma", "PostalCode": "98402" }
                }
                """),
            // A complex object present but with every sub-field null, which Azure Search
            // reports as a null complex field.
            CreateNullableDoc("""
                {
                  "Id": "5", "Name": "Echo", "Category": "Electronics", "Rating": 4,
                  "Tags": [],
                  "Address": { "City": null, "PostalCode": null }
                }
                """),
        ];
    }

    private static Document CreateNullableDoc(string json)
    {
        var index = CreateNullableIndex();
        var item = JsonNode.Parse(json)!.AsObject();
        var doc = new Document();

        foreach (var field in index.Fields)
        {
            var value = item.FirstOrDefault(p =>
                string.Equals(p.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value;

            if (value is null)
            {
                // Matches GetDocFields, which joins on the item's keys and drops null values,
                // so an absent field and an explicit null are indistinguishable in the index.
                continue;
            }

            foreach (var indexField in field.CreateFields(value))
            {
                doc.Add(indexField);
            }
        }

        return doc;
    }

    /// <summary>
    /// Creates an index with a geography point field, used by the geospatial tests.
    /// </summary>
    public static SearchIndex CreateCityIndex()
    {
        return new SearchIndex
        {
            Name = "cities",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Searchable = false, Filterable = true, Sortable = true },
                new SearchField { Name = "Population", Type = "Edm.Int32", Searchable = false, Filterable = true, Sortable = true },
            ]
        };
    }

    /// <summary>
    /// Cities with real coordinates, so the expected distances in the geospatial tests can
    /// be checked against known real-world values.
    /// </summary>
    public static List<Document> CreateCityDocuments()
    {
        return
        [
            // Longitude first, matching the GeoJSON and WKT convention.
            CreateCityDoc("1", "Seattle", -122.3321, 47.6062, 737015),
            CreateCityDoc("2", "Bellevue", -122.2015, 47.6101, 148164),
            CreateCityDoc("3", "Tacoma", -122.4443, 47.2529, 219346),
            CreateCityDoc("4", "Portland", -122.6784, 45.5152, 652503),
            CreateCityDoc("5", "New York", -74.0060, 40.7128, 8336817),
            // A city with no location at all, to cover the null-handling rules.
            CreateCityDocWithoutLocation("6", "Nowhere", 1000),
        ];
    }

    /// <summary>
    /// Creates an index whose geography field is a collection, used by the
    /// Collection(Edm.GeographyPoint) tests.
    /// </summary>
    public static SearchIndex CreateStoreIndex()
    {
        return new SearchIndex
        {
            Name = "stores",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Locations", Type = "Collection(Edm.GeographyPoint)", Searchable = false, Filterable = true },
            ]
        };
    }

    /// <summary>
    /// Chains, each with several branch locations, so that a filter has to consider every
    /// point of a document rather than just the first.
    /// </summary>
    public static List<Document> CreateStoreDocuments()
    {
        return
        [
            // Seattle and Bellevue: both in the Puget Sound area.
            CreateStoreDoc("1", "Puget Chain", [(-122.3321, 47.6062), (-122.2015, 47.6101)]),
            // Portland plus New York: only the far-away point should match a Seattle filter,
            // and only via the second element, which catches readers that stop at the first.
            CreateStoreDoc("2", "Coast To Coast", [(-74.0060, 40.7128), (-122.6784, 45.5152)]),
            // Nowhere near Seattle.
            CreateStoreDoc("3", "East Coast Only", [(-74.0060, 40.7128), (-71.0589, 42.3601)]),
            // An empty collection, which should never match.
            CreateStoreDoc("4", "No Locations", []),
        ];
    }

    private static Document CreateStoreDoc(string id, string name, (double Lon, double Lat)[] locations)
    {
        var doc = new Document
        {
            new StringField("Id", id, Field.Store.YES),
            new TextField("Name", name, Field.Store.YES),
        };

        foreach (var (lon, lat) in locations)
        {
            foreach (var field in GeoSupport.CreateFields("Locations", lon, lat, retrievable: true))
            {
                doc.Add(field);
            }
        }

        return doc;
    }

    private static Document CreateCityDoc(string id, string name, double lon, double lat, int population)
    {
        var doc = new Document
        {
            new StringField("Id", id, Field.Store.YES),
            new TextField("Name", name, Field.Store.YES),
            new Int32Field("Population", population, Field.Store.YES),
        };

        foreach (var field in GeoSupport.CreateFields("Location", lon, lat, retrievable: true))
        {
            doc.Add(field);
        }

        return doc;
    }

    private static Document CreateCityDocWithoutLocation(string id, string name, int population)
    {
        return new Document
        {
            new StringField("Id", id, Field.Store.YES),
            new TextField("Name", name, Field.Store.YES),
            new Int32Field("Population", population, Field.Store.YES),
        };
    }

    /// <summary>
    /// An index carrying one field of every type a scoring function can read, used by the
    /// scoring profile tests (issue #47).
    /// </summary>
    /// <remarks>
    /// All four functions need a field they can act on: a number for <c>magnitude</c>, a date
    /// for <c>freshness</c>, a point for <c>distance</c> and a string collection for
    /// <c>tag</c>. Every one is filterable, which Azure requires of a scoring function's field
    /// and which the emulator needs in order to write the exact-value copies read at query
    /// time.
    /// </remarks>
    public static SearchIndex CreateScoringIndex()
    {
        return new SearchIndex
        {
            Name = "scoring",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true, Filterable = true },
                new SearchField { Name = "Description", Type = "Edm.String", Searchable = true, Filterable = true },
                new SearchField { Name = "Rating", Type = "Edm.Double", Filterable = true, Sortable = true },
                new SearchField { Name = "Updated", Type = "Edm.DateTimeOffset", Filterable = true, Sortable = true },
                new SearchField { Name = "Location", Type = "Edm.GeographyPoint", Filterable = true },
                new SearchField { Name = "Tags", Type = "Collection(Edm.String)", Filterable = true },
                // Numeric collections, which a magnitude function may target: the validator
                // accepts them by their element type, so the query side has to read them by it
                // too.
                new SearchField { Name = "Sizes", Type = "Collection(Edm.Int32)", Filterable = true },
                new SearchField { Name = "Counts", Type = "Collection(Edm.Int64)", Filterable = true },
            ]
        };
    }

    /// <summary>
    /// Documents for <see cref="CreateScoringIndex"/>, all matching the word "widget" so that a
    /// single query returns every one and the ordering between them is decided purely by the
    /// scoring profile under test.
    /// </summary>
    /// <remarks>
    /// The values are spread deliberately: ratings run low to high, dates run old to new, the
    /// points run near to far from Seattle, and the tags overlap only partly. One document
    /// leaves every scored field null, so the rule that a function does not apply to a document
    /// without a value has something to act on.
    ///
    /// <paramref name="now"/> anchors the dates relative to the moment the test runs, since a
    /// freshness function measures against the clock.
    /// </remarks>
    public static List<Document> CreateScoringDocuments(DateTimeOffset now)
    {
        var index = CreateScoringIndex();

        return
        [
            CreateScoringDoc(index, "1", "Widget Basic", 1.0, now.AddDays(-300), (-122.33, 47.60), ["budget"]),
            CreateScoringDoc(index, "2", "Widget Plus", 3.0, now.AddDays(-100), (-122.20, 47.61), ["budget", "popular"]),
            CreateScoringDoc(index, "3", "Widget Pro", 5.0, now.AddDays(-1), (-74.00, 40.71), ["premium", "popular"]),
            CreateScoringDoc(index, "4", "Widget Plain", null, null, null, []),
        ];
    }

    /// <summary>
    /// Documents whose <c>Sizes</c> collection holds its largest value in different positions,
    /// so a test can tell "scored by the largest value" from "scored by the first one written".
    /// </summary>
    public static List<Document> CreateCollectionScoringDocuments()
    {
        var index = CreateScoringIndex();

        return
        [
            CreateCollectionScoringDoc(index, "high-first", [5, 1]),
            CreateCollectionScoringDoc(index, "high-last", [1, 5]),
            CreateCollectionScoringDoc(index, "low", [1, 2]),
        ];
    }

    private static Document CreateCollectionScoringDoc(SearchIndex index, string id, int[] sizes)
    {
        var json = new JsonObject
        {
            ["Id"] = id,
            ["Name"] = "Widget " + id,
            ["Description"] = "a widget for testing",
            ["Sizes"] = new JsonArray(sizes.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()),
        };

        var doc = new Document();

        foreach (var field in index.Fields)
        {
            if (json[field.Name] is not { } value)
            {
                continue;
            }

            foreach (var luceneField in field.CreateFields(value))
            {
                doc.Add(luceneField);
            }
        }

        return doc;
    }

    /// <summary>
    /// Builds a document through the real indexing path, so the tests read the same Lucene
    /// fields a document uploaded over HTTP would produce.
    /// </summary>
    private static Document CreateScoringDoc(
        SearchIndex index,
        string id,
        string name,
        double? rating,
        DateTimeOffset? updated,
        (double Lon, double Lat)? location,
        string[] tags)
    {
        var json = new JsonObject
        {
            ["Id"] = id,
            ["Name"] = name,
            // Shared by every document so one query matches them all.
            ["Description"] = "a widget for testing",
        };

        if (rating != null)
        {
            // Mirrored into the numeric collections so a magnitude function over one of them
            // ranks the documents exactly as the scalar field does.
            json["Sizes"] = new JsonArray((int)rating.Value);
            json["Counts"] = new JsonArray((long)rating.Value);

            json["Rating"] = rating.Value;
        }

        if (updated != null)
        {
            json["Updated"] = updated.Value;
        }

        if (location != null)
        {
            json["Location"] = GeoSupport.CreateGeoJsonPoint(location.Value.Lon, location.Value.Lat);
        }

        if (tags.Length > 0)
        {
            json["Tags"] = new JsonArray(tags.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
        }

        var doc = new Document();

        foreach (var field in index.Fields)
        {
            if (json[field.Name] is not { } value)
            {
                continue;
            }

            foreach (var luceneField in field.CreateFields(value))
            {
                doc.Add(luceneField);
            }
        }

        return doc;
    }

    private static Document CreateProductDoc(string id, string name, string description, double price, string category, bool inStock, int rating)
    {
        var doc = new Document
        {
            new StringField("Id", id, Field.Store.YES),
            new TextField("Name", name, Field.Store.YES),
            new TextField("Description", description, Field.Store.YES),
            new DoubleField("Price", price, Field.Store.YES),
            new StringField("Category", category, Field.Store.YES),
            new Int32Field("InStock", inStock ? 1 : 0, Field.Store.YES),
            new Int32Field("Rating", rating, Field.Store.YES),
        };
        return doc;
    }
}
