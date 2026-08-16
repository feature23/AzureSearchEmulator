using System.Globalization;
using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Core.GeoJson;
using Azure.Search.Documents.Models;

int port = 5123;

if (args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var argPort))
{
    port = argPort;
}
else
{
    Console.WriteLine("Enter HTTPS port number for Azure Search Emulator (default 5123): ");
    var portInput = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedPort))
    {
        port = parsedPort;
    }
}

string endpoint = $"https://localhost:{port}";
const string indexName = "test-index";

var handler = new HttpClientHandler();
// Skip SSL validation for local emulator (self-signed cert). This is safe in test environments only, not for production use.
handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

var credential = new AzureKeyCredential("test-key");

var options = new SearchClientOptions
{
    Transport = new HttpClientTransport(handler),
    Retry = { MaxRetries = 1 }
};

var indexClient = new SearchIndexClient(new Uri(endpoint), credential, options);
var searchClient = new SearchClient(new Uri(endpoint), indexName, credential, options);

try
{
    Console.WriteLine("=== Testing Azure Search Emulator ===\n");

    // Test 1: Create Index
    Console.WriteLine("1. Creating index...");
    var index = new SearchIndex(indexName)
    {
        Fields =
        [
            new SearchField("id", SearchFieldDataType.String) { IsKey = true },
            new SearchField("name", SearchFieldDataType.String) { IsSearchable = true },
            new SearchField("description", SearchFieldDataType.String) { IsSearchable = true },
            new SearchField("price", SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true },
            new SearchField("category", SearchFieldDataType.String) { IsFilterable = true },
            new SearchField("inStock", SearchFieldDataType.Boolean) { IsFilterable = true },
            new SearchField("location", SearchFieldDataType.GeographyPoint) { IsFilterable = true, IsSortable = true }
        ]
    };

    try
    {
        var created = await indexClient.CreateIndexAsync(index);
        Console.WriteLine($"   ✓ Index created: {created.Value.Name}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 2: Get Document Count
    Console.WriteLine("2. Getting document count...");
    try
    {
        var count = await searchClient.GetDocumentCountAsync();
        Console.WriteLine($"   ✓ Document count: {count.Value}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 3: Upload Documents
    Console.WriteLine("3. Uploading documents...");
    try
    {
        // Locations are Seattle, Bellevue (~10km away), and Portland (~234km away).
        var docs = new List<object>
        {
            // GeoPoint takes longitude first, matching the GeoJSON wire format.
            new { id = "1", name = "Laptop Pro 15", description = "High-performance laptop with 16GB RAM and 512GB SSD", price = 1299.99, category = "Electronics", inStock = true, location = new GeoPoint(-122.3321, 47.6062) },
            new { id = "2", name = "Laptop Budget 13", description = "Affordable laptop perfect for students and everyday use", price = 499.99, category = "Electronics", inStock = true, location = new GeoPoint(-122.2015, 47.6101) },
            new { id = "3", name = "Gaming Mouse", description = "Precision gaming mouse with 16000 DPI sensor", price = 59.99, category = "Accessories", inStock = true, location = new GeoPoint(-122.6784, 45.5152) },
        };

        var batch = IndexDocumentsBatch.Upload<object>(docs);
        var result = await searchClient.IndexDocumentsAsync(batch);
        Console.WriteLine($"   ✓ Uploaded {result.Value.Results.Count} documents\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 4: Get Document Count After Upload
    Console.WriteLine("4. Getting document count after upload...");
    try
    {
        var count = await searchClient.GetDocumentCountAsync();
        Console.WriteLine($"   ✓ Document count: {count.Value}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 5: Get Document by ID
    Console.WriteLine("5. Getting document by ID...");
    try
    {
        var doc = await searchClient.GetDocumentAsync<object>("1");
        Console.WriteLine($"   ✓ Retrieved document with ID: 1");
        Console.WriteLine($"   Document: {System.Text.Json.JsonSerializer.Serialize(doc)}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 6: Search Documents
    Console.WriteLine("6. Searching documents...");
    try
    {
        var options2 = new SearchOptions { Size = 10, SearchFields = { "name", "description" } };
        var results = await searchClient.SearchAsync<object>("laptop", options2);
        var items = await results.Value.GetResultsAsync().ToListAsync();
        Console.WriteLine($"   ✓ Found {items.Count} results");
        foreach (var item in items)
        {
            Console.WriteLine($"     - {System.Text.Json.JsonSerializer.Serialize(item.Document)}");
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 7: Geospatial filter and distance ordering
    Console.WriteLine("7. Searching by distance...");
    try
    {
        // Seattle; a 20km radius should include Seattle and Bellevue but not Portland.
        const string origin = "geography'POINT(-122.3321 47.6062)'";

        var geoOptions = new SearchOptions
        {
            Size = 10,
            Filter = $"geo.distance(location, {origin}) le 20",
            OrderBy = { $"geo.distance(location, {origin}) asc" }
        };

        var results = await searchClient.SearchAsync<object>("*", geoOptions);
        var items = await results.Value.GetResultsAsync().ToListAsync();

        Console.WriteLine($"   ✓ Found {items.Count} results within 20km, nearest first");
        foreach (var item in items)
        {
            Console.WriteLine($"     - {System.Text.Json.JsonSerializer.Serialize(item.Document)}");
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 8: Geospatial polygon intersection
    Console.WriteLine("8. Searching within a polygon...");
    try
    {
        var geoOptions = new SearchOptions
        {
            Size = 10,
            // A counterclockwise box around the Seattle/Bellevue area.
            Filter = "geo.intersects(location, geography'POLYGON((-122.5 47.5, -122.1 47.5, -122.1 47.7, -122.5 47.7, -122.5 47.5))')"
        };

        var results = await searchClient.SearchAsync<object>("*", geoOptions);
        var items = await results.Value.GetResultsAsync().ToListAsync();

        Console.WriteLine($"   ✓ Found {items.Count} results within the polygon");
        foreach (var item in items)
        {
            Console.WriteLine($"     - {System.Text.Json.JsonSerializer.Serialize(item.Document)}");
        }
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }

    // Test 9: Delete Index
    Console.WriteLine("9. Deleting index...");
    try
    {
        await indexClient.DeleteIndexAsync(indexName);
        Console.WriteLine($"   ✓ Index deleted\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   ✗ Error: {ex.GetType().Name}: {ex.Message}\n");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal error: {ex}");
}
