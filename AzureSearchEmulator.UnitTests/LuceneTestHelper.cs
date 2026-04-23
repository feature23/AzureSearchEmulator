using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
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
