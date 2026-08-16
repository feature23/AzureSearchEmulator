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
