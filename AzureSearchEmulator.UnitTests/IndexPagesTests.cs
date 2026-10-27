using Azure.Search.Documents.Indexes.Models;
using AzureSearchEmulator.Dashboard;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Store;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Tests for the pieces behind the dashboard's index pages (issue #91) that do not need a running
/// emulator: how a definition is laid out as a table, and how a document is pulled out of a
/// response for its raw view.
/// </summary>
public class IndexPagesTests
{
    [Fact]
    public void Flatten_PutsSubfieldsAfterTheirParent()
    {
        var fields = new List<SearchField>
        {
            new("id", SearchFieldDataType.String) { IsKey = true, IsSortable = true },
            new("address", SearchFieldDataType.Complex)
            {
                Fields =
                {
                    new SearchField("city", SearchFieldDataType.String) { IsFilterable = true },
                    new SearchField("geo", SearchFieldDataType.Complex)
                    {
                        Fields = { new SearchField("lat", SearchFieldDataType.Double) }
                    }
                }
            },
            new("rating", SearchFieldDataType.Int32) { IsHidden = true }
        };

        var rows = IndexSchema.Flatten(fields);

        Assert.Equal(["id", "address", "address/city", "address/geo", "address/geo/lat", "rating"], rows.Select(i => i.Path));
        Assert.Equal([0, 0, 1, 1, 2, 0], rows.Select(i => i.Depth));
        Assert.True(rows[0].IsKey);
        Assert.True(rows[1].IsComplex);
        Assert.False(rows[2].IsComplex);
        Assert.True(rows[2].Filterable);
        Assert.False(rows[5].Retrievable);
        Assert.Null(rows[2].Retrievable);
    }

    [Fact]
    public void Flatten_DescribesAnalyzersAndVectors()
    {
        var fields = new List<SearchField>
        {
            new("plain", SearchFieldDataType.String) { AnalyzerName = LexicalAnalyzerName.EnLucene },
            new("split", SearchFieldDataType.String)
            {
                IndexAnalyzerName = LexicalAnalyzerName.Keyword,
                SearchAnalyzerName = LexicalAnalyzerName.StandardLucene
            },
            new("vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                VectorSearchDimensions = 3,
                VectorSearchProfileName = "hnsw"
            }
        };

        var rows = IndexSchema.Flatten(fields);

        Assert.Equal("en.lucene", rows[0].Analyzer);
        Assert.Equal("index: keyword, search: standard.lucene", rows[1].Analyzer);
        Assert.Null(rows[2].Analyzer);
        Assert.Equal(3, rows[2].Dimensions);
        Assert.Equal("hnsw", rows[2].VectorProfile);
        Assert.Equal("Collection(Edm.Single)", rows[2].Type);
    }

    [Fact]
    public void IsKeySortable_FollowsTheKeyField()
    {
        var sortable = new SearchIndex("a") { Fields = { new SearchField("id", SearchFieldDataType.String) { IsKey = true, IsSortable = true } } };
        var unsortable = new SearchIndex("b") { Fields = { new SearchField("id", SearchFieldDataType.String) { IsKey = true } } };

        Assert.True(IndexSchema.IsKeySortable(sortable));
        Assert.False(IndexSchema.IsKeySortable(unsortable));
        Assert.Equal("id", IndexSchema.KeyField(sortable));
    }

    [Fact]
    public void DocumentJson_TakesTheFirstSearchResultWithoutAnnotations()
    {
        var json = DocumentJson.Extract("""{"@odata.count": 2, "value": [{"@search.score": 1.0, "id": "1", "name": "Café <b>"}]}""");

        Assert.Equal("""{"id":"1","name":"Café <b>"}""", json);
    }

    [Fact]
    public void DocumentJson_TakesALookedUpDocumentAsIs()
    {
        Assert.Equal("""{"id":"1"}""", DocumentJson.Extract("""{"id": "1"}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"value": []}""")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    public void DocumentJson_WithNoDocument_IsNull(string? body)
    {
        Assert.Null(DocumentJson.Extract(body));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(15 * 1024 + 512, "16 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5 GB")]
    public void ByteSize_UsesTheLargestWholeUnit(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void LuceneStorage_SumsTheDirectorysFiles()
    {
        using var directory = new RAMDirectory();

        foreach (var (name, length) in new[] { ("a", 10), ("b", 32) })
        {
            using var output = directory.CreateOutput(name, IOContext.DEFAULT);
            output.WriteBytes(new byte[length], length);
        }

        Assert.Equal(42, LuceneStorage.GetSize(directory));
    }

    /// <remarks>
    /// An index nothing has been written to yet has no folder on disk.
    /// </remarks>
    [Fact]
    public void LuceneStorage_OfAMissingDirectory_IsZero()
    {
        using var directory = new SimpleFSDirectory(Path.Combine(Path.GetTempPath(), "azs-missing-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(0, LuceneStorage.GetSize(directory));
    }
}
