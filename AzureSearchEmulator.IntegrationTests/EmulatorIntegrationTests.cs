using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Integration tests for the Azure Search Emulator using the Azure Search SDK.
/// Tests run against a containerized instance of the emulator using Testcontainers.
/// Covers index creation, document indexing, and search/retrieval operations using a product e-commerce domain.
/// </summary>
public class EmulatorIntegrationTests(EmulatorFactory factory)
    : IClassFixture<EmulatorFactory>
{
    [Fact]
    public async Task CreateIndex_ShouldSucceed()
    {
        // Arrange
        const string indexName = "test-create-index";
        var indexClient = factory.CreateSearchIndexClient();

        // Act
        var result = await CreateIndexAsync(indexClient, indexName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(indexName, result.Name);

        // Verify the index exists
        var retrievedIndex = await indexClient.GetIndexAsync(indexName);
        Assert.NotNull(retrievedIndex);
        Assert.Equal(indexName, retrievedIndex.Value.Name);

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task UploadDocuments_ShouldSucceed()
    {
        const string indexName = "test-upload-documents";
        var indexClient = factory.CreateSearchIndexClient();

        // Arrange - Create index first
        await CreateIndexAsync(indexClient, indexName);

        var documents = CreateProductDocuments();

        // Act
        var searchClient = factory.CreateSearchClient(indexName);
        var batch = IndexDocumentsBatch.Upload(documents);
        var result = await searchClient.IndexDocumentsAsync(batch);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Value.Results.All(r => r.Succeeded), "All documents should be successfully indexed");

        // Verify document count
        var countResult = await searchClient.GetDocumentCountAsync();
        Assert.Equal(5, countResult.Value);

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task GetDocumentById_ShouldReturnDocument()
    {
        const string indexName = "test-get-document";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act
        var document = await searchClient.GetDocumentAsync<Product>("1");

        // Assert
        Assert.NotNull(document);
        Assert.Equal("1", document.Value.Id);
        Assert.Equal("Laptop Pro 15", document.Value.Name);
        Assert.Equal("High-performance laptop with 16GB RAM and 512GB SSD", document.Value.Description);

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SimpleQuery_ShouldReturnResults()
    {
        const string indexName = "test-simple-search";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act
        var options = new SearchOptions
        {
            SearchFields = { "name", "description" },
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("laptop", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.Contains(items, r => r.Document.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase));

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_WithFilter_ShouldReturnResults()
    {
        const string indexName = "test-filter-search";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Filter by inStock field
        var options = new SearchOptions
        {
            Filter = "InStock eq true",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.All(r => r.Document.InStock), "All results should have inStock = true");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_WithSorting_ShouldReturnSortedResults()
    {
        const string indexName = "test-sort-search";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Sort by price descending
        var options = new SearchOptions
        {
            OrderBy = { "price desc" },
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);

        // Verify descending order
        double? previousPrice = null;
        foreach (var item in items)
        {
            if (previousPrice.HasValue)
            {
                Assert.True(item.Document.Price <= previousPrice, "Results should be sorted by price descending");
            }
            previousPrice = item.Document.Price;
        }

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_WithPaging_ShouldReturnPagedResults()
    {
        const string indexName = "test-paging-search";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Get first page
        var firstPageOptions = new SearchOptions
        {
            Size = 2,
            Skip = 0
        };
        var firstPageResults = await searchClient.SearchAsync<Product>("*:*", firstPageOptions);
        var firstPageItems = await firstPageResults.Value.GetResultsAsync().ToListAsync();

        // Assert first page
        Assert.NotNull(firstPageItems);
        Assert.Equal(2, firstPageItems.Count);
        var firstPageId = firstPageItems[0].Document.Id;

        // Act - Get second page
        var secondPageOptions = new SearchOptions
        {
            Size = 2,
            Skip = 2
        };
        var secondPageResults = await searchClient.SearchAsync<Product>("*", secondPageOptions);
        var secondPageItems = await secondPageResults.Value.GetResultsAsync().ToListAsync();

        // Assert second page
        Assert.NotNull(secondPageItems);
        Assert.Equal(2, secondPageItems.Count);
        var secondPageId = secondPageItems[0].Document.Id;

        // Verify different pages
        Assert.NotEqual(firstPageId, secondPageId);

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task DeleteIndex_ShouldSucceed()
    {
        const string indexName = "test-delete-index";
        var indexClient = factory.CreateSearchIndexClient();

        // Arrange - Create index first
        await CreateIndexAsync(indexClient, indexName);

        // Act
        await indexClient.DeleteIndexAsync(indexName);

        // Assert - Verify index is deleted
        var exception = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            async () => await indexClient.GetIndexAsync(indexName)
        );
        Assert.Equal(404, exception.Status);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatch_BasicQuery_ShouldReturnResults()
    {
        const string indexName = "test-ismatch-basic";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Use search.ismatch to search for 'laptop' in all searchable fields
        var options = new SearchOptions
        {
            Filter = "search.ismatch('laptop')",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.Any(r => r.Document.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)),
            "Should return documents with 'laptop' in Name or Description");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatch_WithFieldName_ShouldReturnResults()
    {
        const string indexName = "test-ismatch-field";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Search in specific field using search.ismatch(search, field)
        var options = new SearchOptions
        {
            Filter = "search.ismatch('laptop', 'Name')",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.All(r => r.Document.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)),
            "All results should have 'laptop' in Name field");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatch_WithMultipleFields_ShouldReturnResults()
    {
        const string indexName = "test-ismatch-multi-field";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Search in multiple fields using search.ismatch(search, fields)
        var options = new SearchOptions
        {
            Filter = "search.ismatch('16000 DPI', 'Name,Description')",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.Contains(items, r => r.Document.Name == "Gaming Mouse" || r.Document.Description.Contains("16000 DPI"));

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatch_WithBooleanOperator_ShouldReturnResults()
    {
        const string indexName = "test-ismatch-and";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Combine search.ismatch with other filters using AND
        var options = new SearchOptions
        {
            Filter = "search.ismatch('laptop') and InStock eq true",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.All(r => r.Document.InStock), "All results should have InStock = true");
        Assert.True(items.All(r => r.Document.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
                                   r.Document.Description.Contains("Laptop", StringComparison.OrdinalIgnoreCase)),
            "All results should contain 'laptop'");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatch_WithNegation_ShouldReturnResults()
    {
        const string indexName = "test-ismatch-not";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Use NOT to exclude search.ismatch results
        var options = new SearchOptions
        {
            Filter = "not search.ismatch('keyboard')",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.All(r => !r.Document.Name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) &&
                                   !r.Document.Description.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)),
            "All results should NOT contain 'keyboard'");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    [Fact]
    public async Task SearchDocuments_SearchIsMatchScoring_ShouldReturnResults()
    {
        const string indexName = "test-ismatchscoring";
        var indexClient = factory.CreateSearchIndexClient();
        var searchClient = factory.CreateSearchClient(indexName);

        // Arrange - Create index and upload documents
        await CreateIndexAsync(indexClient, indexName);
        await UploadDocumentsAsync(searchClient);

        // Act - Use search.ismatchscoring (scoring variant)
        var options = new SearchOptions
        {
            Filter = "search.ismatchscoring('laptop')",
            Size = 50
        };
        var results = await searchClient.SearchAsync<Product>("*", options);

        // Assert
        Assert.NotNull(results);
        var resultsList = results.Value.GetResultsAsync();
        var items = await resultsList.ToListAsync();
        Assert.NotEmpty(items);
        Assert.True(items.Any(r => r.Document.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)),
            "Should return documents with 'laptop'");

        // Cleanup
        await indexClient.DeleteIndexAsync(indexName);
    }

    // Helper Methods

    private static async Task<SearchIndex> CreateIndexAsync(SearchIndexClient indexClient, string indexName)
    {
        // Clean up any existing index
        try
        {
            await indexClient.DeleteIndexAsync(indexName);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            // Index doesn't exist, which is fine
        }

        var index = new SearchIndex(indexName)
        {
            Fields =
            [
                new SearchField(nameof(Product.Id), SearchFieldDataType.String) { IsKey = true, IsStored = true, IsSearchable = true, IsFilterable = true},
                new SearchField(nameof(Product.Name), SearchFieldDataType.String) { IsSearchable = true, IsStored = true },
                new SearchField(nameof(Product.Description), SearchFieldDataType.String) { IsSearchable = true, IsStored = true},
                new SearchField(nameof(Product.Price), SearchFieldDataType.Double) { IsFilterable = true, IsSortable = true, IsStored = true },
                new SearchField(nameof(Product.Category), SearchFieldDataType.String) { IsFilterable = true, IsStored = true },
                new SearchField(nameof(Product.InStock), SearchFieldDataType.Boolean) { IsFilterable = true, IsStored = true }
            ]
        };

        await indexClient.CreateIndexAsync(index);

        return index;
    }

    private static async Task UploadDocumentsAsync(SearchClient searchClient)
    {
        var documents = CreateProductDocuments();
        var batch = IndexDocumentsBatch.Upload(documents);
        await searchClient.IndexDocumentsAsync(batch);
    }

    private static List<Product> CreateProductDocuments()
    {
        return
        [
            new Product
            {
                Id = "1",
                Name = "Laptop Pro 15",
                Description = "High-performance laptop with 16GB RAM and 512GB SSD",
                Price = 1299.99,
                Category = "Electronics",
                InStock = true
            },
            new Product
            {
                Id = "2",
                Name = "Laptop Budget 13",
                Description = "Affordable laptop perfect for students and everyday use",
                Price = 499.99,
                Category = "Electronics",
                InStock = true
            },
            new Product
            {
                Id = "3",
                Name = "Gaming Mouse",
                Description = "Precision gaming mouse with 16000 DPI sensor",
                Price = 59.99,
                Category = "Accessories",
                InStock = true
            },
            new Product
            {
                Id = "4",
                Name = "Mechanical Keyboard",
                Description = "Mechanical keyboard with Cherry MX switches and RGB lighting",
                Price = 149.99,
                Category = "Accessories",
                InStock = false
            },
            new Product
            {
                Id = "5",
                Name = "Monitor 4K",
                Description = "27-inch 4K monitor with 60Hz refresh rate",
                Price = 599.99,
                Category = "Electronics",
                InStock = true
            }
        ];
    }
}
