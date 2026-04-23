using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Unit tests for LuceneNetIndexSearcher, verifying end-to-end search behavior
/// including $search, $filter, $orderby, paging, and their combinations.
/// Uses a stub ILuceneIndexReaderFactory backed by an in-memory Lucene index.
/// </summary>
public class LuceneNetIndexSearcherTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcherService;

    public LuceneNetIndexSearcherTests()
    {
        var index = LuceneTestHelper.CreateProductIndex();
        _helper = new LuceneTestHelper(index, LuceneTestHelper.CreateProductDocuments());

        var readerFactory = new StubIndexReaderFactory(_helper.Directory);
        _searcherService = new LuceneNetIndexSearcher(readerFactory);
    }

    public void Dispose()
    {
        _helper.Dispose();
    }

    // ===== Basic search ($search) =====

    [Fact]
    public async Task Search_MatchAll_ReturnsAllDocuments()
    {
        var request = new SearchRequest { Search = "*", Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
    }

    [Fact]
    public async Task Search_NullSearch_ReturnsAllDocuments()
    {
        var request = new SearchRequest { Search = null, Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
    }

    [Fact]
    public async Task Search_SimpleTextQuery_ReturnsMatchingDocuments()
    {
        var request = new SearchRequest { Search = "laptop", Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.True(response.Results.Count >= 2); // Both laptops
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmptyResults()
    {
        var request = new SearchRequest { Search = "xyznonexistent", Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Search_WithSearchFields_RestrictsToSpecifiedFields()
    {
        var request = new SearchRequest
        {
            Search = "laptop",
            SearchFields = "Name",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            var name = r["Name"]?.GetValue<string>() ?? "";
            Assert.Contains("Laptop", name, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ===== Filter only ($filter) =====

    [Fact]
    public async Task Search_FilterOnly_BooleanField_ReturnsFilteredDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "InStock eq true",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(4, response.Results.Count);
        Assert.All(response.Results, r =>
        {
            var inStock = r["InStock"]?.GetValue<bool>() ?? false;
            Assert.True(inStock);
        });
    }

    [Fact]
    public async Task Search_FilterOnly_StringField_ReturnsFilteredDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category eq 'Accessories'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r =>
        {
            var category = r["Category"]?.GetValue<string>() ?? "";
            Assert.Equal("Accessories", category);
        });
    }

    [Fact]
    public async Task Search_FilterOnly_IntegerRange_ReturnsFilteredDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Rating lt 4",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        Assert.Equal("Monitor 4K", response.Results[0]["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Search_Filter_NotEqual_ExcludesMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category ne 'Electronics'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r =>
        {
            var category = r["Category"]?.GetValue<string>() ?? "";
            Assert.NotEqual("Electronics", category);
        });
    }

    [Fact]
    public async Task Search_Filter_NotOperatorWithParens()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "not (InStock eq true)",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        Assert.Equal("Mechanical Keyboard", response.Results[0]["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Search_Filter_NotOperatorWithoutParens()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "not InStock eq true",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        Assert.Equal("Mechanical Keyboard", response.Results[0]["Name"]?.GetValue<string>());
    }

    // ===== Search + Filter combined =====

    [Fact]
    public async Task Search_TextQueryWithFilter_ReturnsBothConstraintsMet()
    {
        var request = new SearchRequest
        {
            Search = "laptop",
            Filter = "Rating gt 4",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            var rating = r["Rating"]?.GetValue<int>() ?? 0;
            Assert.True(rating > 4);
        });
    }

    [Fact]
    public async Task Search_TextQueryWithFilter_NarrowsResults()
    {
        // Search for "laptop" (should get 2+), filter by Rating lt 5 (should narrow to Budget Laptop)
        var request = new SearchRequest
        {
            Search = "laptop",
            Filter = "Rating lt 5",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            var rating = r["Rating"]?.GetValue<int>() ?? 0;
            Assert.True(rating < 5);
        });
    }

    // ===== Filter with search.ismatch =====

    [Fact]
    public async Task Search_FilterSearchIsMatch_ReturnsMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "search.ismatch('laptop')",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
    }

    [Fact]
    public async Task Search_FilterSearchIsMatch_WithBooleanFilter_ReturnsIntersection()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "search.ismatch('laptop') and InStock eq true",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            var inStock = r["InStock"]?.GetValue<bool>() ?? false;
            Assert.True(inStock);
        });
    }

    [Fact]
    public async Task Search_FilterSearchIsMatchScoring_ReturnsMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "search.ismatchscoring('monitor')",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
    }

    // ===== search.in function =====

    [Fact]
    public async Task Search_FilterSearchIn_ReturnsMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "search.in(Category, 'Accessories')",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
    }

    // ===== AND / OR filter combinations =====

    [Fact]
    public async Task Search_FilterAnd_ReturnsIntersection()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category eq 'Accessories' and InStock eq true",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        Assert.Equal("Gaming Mouse", response.Results[0]["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task Search_FilterOr_ReturnsUnion()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category eq 'Accessories' or Rating gt 4",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        // Accessories: Gaming Mouse (3), Mechanical Keyboard (4)
        // Rating > 4: Laptop Pro (1), Mechanical Keyboard (4)
        // Union: 1, 3, 4
        Assert.Equal(3, response.Results.Count);
    }

    // ===== Sorting ($orderby) =====

    [Fact]
    public async Task Search_OrderByPriceAsc_ReturnsSortedResults()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Orderby = "Price asc",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
        double? prev = null;
        foreach (var result in response.Results)
        {
            var price = result["Price"]?.GetValue<double>() ?? 0;
            if (prev.HasValue)
            {
                Assert.True(price >= prev, $"Expected {price} >= {prev}");
            }
            prev = price;
        }
    }

    [Fact]
    public async Task Search_OrderByPriceDesc_ReturnsSortedResults()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Orderby = "Price desc",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
        double? prev = null;
        foreach (var result in response.Results)
        {
            var price = result["Price"]?.GetValue<double>() ?? 0;
            if (prev.HasValue)
            {
                Assert.True(price <= prev, $"Expected {price} <= {prev}");
            }
            prev = price;
        }
    }

    [Fact]
    public async Task Search_OrderByRatingAsc_ReturnsSortedResults()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Orderby = "Rating asc",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
        int? prev = null;
        foreach (var result in response.Results)
        {
            var rating = result["Rating"]?.GetValue<int>() ?? 0;
            if (prev.HasValue)
            {
                Assert.True(rating >= prev, $"Expected {rating} >= {prev}");
            }
            prev = rating;
        }
    }

    // ===== Paging (skip/top) =====

    [Fact]
    public async Task Search_Paging_FirstPage_ReturnsCorrectCount()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Top = 2,
            Skip = 0
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task Search_Paging_SecondPage_ReturnsCorrectCount()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Top = 2,
            Skip = 2
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task Search_Paging_LastPage_ReturnsRemainder()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Top = 2,
            Skip = 4
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
    }

    [Fact]
    public async Task Search_Paging_BeyondEnd_ReturnsEmpty()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Top = 10,
            Skip = 100
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Search_Paging_PagesDoNotOverlap()
    {
        var page1Request = new SearchRequest { Search = "*", Orderby = "Price asc", Top = 2, Skip = 0 };
        var page2Request = new SearchRequest { Search = "*", Orderby = "Price asc", Top = 2, Skip = 2 };

        var page1 = await _searcherService.Search(_helper.Index, page1Request);
        var page2 = await _searcherService.Search(_helper.Index, page2Request);

        var page1Ids = page1.Results.Select(r => r["Id"]?.GetValue<string>()).ToHashSet();
        var page2Ids = page2.Results.Select(r => r["Id"]?.GetValue<string>()).ToHashSet();

        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    // ===== Count =====

    [Fact]
    public async Task Search_WithCount_ReturnsTotal()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Count = true,
            Top = 2
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Count);
        Assert.Equal(2, response.Results.Count);
    }

    [Fact]
    public async Task Search_WithoutCount_CountIsNotSet()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Count = false,
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Null(response.Count);
    }

    [Fact]
    public async Task Search_WithCountAndFilter_ReturnsFilteredTotal()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "Category eq 'Electronics'",
            Count = true,
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(3, response.Count);
        Assert.Equal(3, response.Results.Count);
    }

    // ===== GetDocCount =====

    [Fact]
    public async Task GetDocCount_ReturnsCorrectCount()
    {
        var count = await _searcherService.GetDocCount(_helper.Index);
        Assert.Equal(5, count);
    }

    // ===== GetDoc =====

    [Fact]
    public async Task GetDoc_ExistingDocument_ReturnsDocument()
    {
        var doc = await _searcherService.GetDoc(_helper.Index, "1");

        Assert.NotNull(doc);
        Assert.Equal("Laptop Pro 15", doc["Name"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetDoc_NonExistingDocument_ReturnsNull()
    {
        var doc = await _searcherService.GetDoc(_helper.Index, "999");
        Assert.Null(doc);
    }

    // ===== SearchMode =====

    [Fact]
    public async Task Search_SearchModeAny_MatchesAnyTerm()
    {
        var request = new SearchRequest
        {
            Search = "laptop gaming",
            SearchMode = "any",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        // "any" mode: should match docs containing "laptop" OR "gaming"
        Assert.True(response.Results.Count >= 2);
    }

    [Fact]
    public async Task Search_SearchModeAll_RequiresAllTerms()
    {
        var request = new SearchRequest
        {
            Search = "laptop gaming",
            SearchMode = "all",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        // "all" mode: requires both "laptop" AND "gaming" - unlikely to match
        Assert.True(response.Results.Count < 5);
    }

    // ===== No searchable fields =====

    [Fact]
    public async Task Search_NoSearchableFields_Throws()
    {
        var index = new SearchIndex
        {
            Name = "no-searchable",
            Fields = [new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false }]
        };

        var readerFactory = new StubIndexReaderFactory(_helper.Directory);
        var searcher = new LuceneNetIndexSearcher(readerFactory);
        var request = new SearchRequest { Search = "test", Top = 50 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => searcher.Search(index, request));
    }

    // ===== Null filter =====

    [Fact]
    public async Task Search_NullFilter_ReturnsAllDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = null,
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
    }

    [Fact]
    public async Task Search_EmptyFilter_ReturnsAllDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
    }

    // ===== Search score =====

    [Fact]
    public async Task Search_ReturnsSearchScore()
    {
        var request = new SearchRequest { Search = "laptop", Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.NotEmpty(response.Results);
        Assert.All(response.Results, r =>
        {
            Assert.True(r.ContainsKey("@search.score"));
            var score = r["@search.score"]?.GetValue<float>() ?? 0;
            Assert.True(score > 0);
        });
    }

    // ===== Wildcard search =====

    [Fact]
    public async Task Search_WildcardStar_ReturnsAllDocuments()
    {
        var request = new SearchRequest { Search = "*:*", Top = 50 };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(5, response.Results.Count);
    }

    /// <summary>
    /// Stub implementation of ILuceneIndexReaderFactory backed by a RAMDirectory.
    /// </summary>
    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName)
        {
            return DirectoryReader.Open(directory);
        }

        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);

        public void ClearCachedReader(string indexName) { }
    }
}

/// <summary>
/// Tests for filtering with GUID string values through the full LuceneNetIndexSearcher pipeline.
/// Verifies that $filter with string equality on filterable (non-searchable) fields
/// containing GUIDs works correctly, both standalone and combined with $search.
/// </summary>
public class LuceneNetIndexSearcher_GuidFilterTests : IDisposable
{
    private readonly LuceneTestHelper _helper;
    private readonly LuceneNetIndexSearcher _searcherService;

    private const string Guid1 = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string Guid2 = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string Guid3 = "c3d4e5f6-a7b8-9012-cdef-123456789012";

    public LuceneNetIndexSearcher_GuidFilterTests()
    {
        var index = CreateIndex();
        _helper = new LuceneTestHelper(index, CreateDocuments());
        var readerFactory = new StubIndexReaderFactory(_helper.Directory);
        _searcherService = new LuceneNetIndexSearcher(readerFactory);
    }

    public void Dispose()
    {
        _helper.Dispose();
    }

    private static SearchIndex CreateIndex()
    {
        return new SearchIndex
        {
            Name = "items",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true },
                new SearchField { Name = "ProductId", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Type", Type = "Edm.String", Searchable = false, Filterable = true },
                new SearchField { Name = "Name", Type = "Edm.String", Searchable = true },
                new SearchField { Name = "Description", Type = "Edm.String", Searchable = true },
            ]
        };
    }

    private static List<Lucene.Net.Documents.Document> CreateDocuments()
    {
        return
        [
            CreateDoc("1", Guid1, "Product", "Widget Alpha", "A premium widget for all occasions"),
            CreateDoc("2", Guid2, "Product", "Widget Beta", "An affordable widget for everyday use"),
            CreateDoc("3", Guid3, "Variant", "Widget Gamma", "A variant of the gamma widget line"),
            CreateDoc("4", Guid1, "Variant", "Widget Delta", "A delta variant of the alpha product"),
            CreateDoc("5", Guid2, "Accessory", "Widget Cable", "USB cable for connecting widgets"),
        ];
    }

    private static Lucene.Net.Documents.Document CreateDoc(string id, string productId, string type, string name, string description)
    {
        return new Lucene.Net.Documents.Document
        {
            new Lucene.Net.Documents.StringField("Id", id, Lucene.Net.Documents.Field.Store.YES),
            new Lucene.Net.Documents.StringField("ProductId", productId, Lucene.Net.Documents.Field.Store.YES),
            new Lucene.Net.Documents.StringField("Type", type, Lucene.Net.Documents.Field.Store.YES),
            new Lucene.Net.Documents.TextField("Name", name, Lucene.Net.Documents.Field.Store.YES),
            new Lucene.Net.Documents.TextField("Description", description, Lucene.Net.Documents.Field.Store.YES),
        };
    }

    // ===== Filter-only tests (Search = "*") =====

    [Fact]
    public async Task Filter_GuidEquality_ReturnsMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId eq '{Guid1}'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Equal("1", ids[0]);
        Assert.Equal("4", ids[1]);
    }

    [Fact]
    public async Task Filter_GuidEquality_NoMatch_ReturnsEmpty()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = "ProductId eq '00000000-0000-0000-0000-000000000000'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Filter_CompoundOrWithGuid_ReturnsCorrectResults()
    {
        // The exact pattern reported: ProductId eq '{id}' or (Type eq 'Product' and Id eq '{id}')
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId eq '{Guid1}' or (Type eq 'Product' and Id eq '2')",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal("1", ids[0]);  // ProductId matches Guid1
        Assert.Equal("2", ids[1]);  // Type=Product AND Id=2
        Assert.Equal("4", ids[2]);  // ProductId matches Guid1
    }

    [Fact]
    public async Task Filter_GuidEqualityAndType_ReturnsIntersection()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId eq '{Guid1}' and Type eq 'Variant'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        Assert.Equal("4", response.Results[0]["Id"]?.GetValue<string>());
    }

    [Fact]
    public async Task Filter_GuidNotEqual_ExcludesMatchingDocuments()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId ne '{Guid1}'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(3, response.Results.Count);
        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Equal("2", ids[0]);
        Assert.Equal("3", ids[1]);
        Assert.Equal("5", ids[2]);
    }

    // ===== Search + Filter combined =====

    [Fact]
    public async Task Search_TextWithGuidFilter_ReturnsIntersection()
    {
        // Search for "widget" (all docs match) but filter to only Guid1 products
        var request = new SearchRequest
        {
            Search = "widget",
            Filter = $"ProductId eq '{Guid1}'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Results.Count);
        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Equal("1", ids[0]);
        Assert.Equal("4", ids[1]);
    }

    [Fact]
    public async Task Search_TextWithGuidFilter_NarrowsSearchResults()
    {
        // Search for "cable" (matches doc 5) but filter to Guid1 (docs 1,4) — no overlap
        var request = new SearchRequest
        {
            Search = "cable",
            Filter = $"ProductId eq '{Guid1}'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Search_TextWithCompoundGuidOrFilter_ReturnsCorrectResults()
    {
        // Search for "variant" combined with the compound OR filter pattern
        var request = new SearchRequest
        {
            Search = "variant",
            Filter = $"ProductId eq '{Guid1}' or (Type eq 'Product' and Id eq '2')",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        // "variant" appears in docs 3 and 4 (Name/Description)
        // Filter allows docs 1, 2, 4
        // Intersection: doc 4
        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Contains("4", ids);
    }

    [Fact]
    public async Task Filter_GuidEquality_WithCount_ReturnsCorrectCount()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId eq '{Guid2}'",
            Count = true,
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Equal(2, response.Count);
        Assert.Equal(2, response.Results.Count);
        var ids = response.Results.Select(r => r["Id"]?.GetValue<string>()).OrderBy(id => id).ToList();
        Assert.Equal("2", ids[0]);
        Assert.Equal("5", ids[1]);
    }

    [Fact]
    public async Task Filter_GuidEquality_ReturnsCorrectFieldValues()
    {
        var request = new SearchRequest
        {
            Search = "*",
            Filter = $"ProductId eq '{Guid3}'",
            Top = 50
        };
        var response = await _searcherService.Search(_helper.Index, request);

        Assert.Single(response.Results);
        var result = response.Results[0];
        Assert.Equal("3", result["Id"]?.GetValue<string>());
        Assert.Equal(Guid3, result["ProductId"]?.GetValue<string>());
        Assert.Equal("Variant", result["Type"]?.GetValue<string>());
        Assert.Equal("Widget Gamma", result["Name"]?.GetValue<string>());
    }

    private class StubIndexReaderFactory(Lucene.Net.Store.Directory directory) : ILuceneIndexReaderFactory
    {
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
