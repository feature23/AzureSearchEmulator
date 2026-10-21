using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Xunit;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers the next page a search offers, which the controller returns as
/// <c>@search.nextPageParameters</c>.
/// </summary>
/// <remarks>
/// Azure offers one only when the request leaves out <c>top</c>: it then returns 50 results and,
/// if more match, a continuation. A request that sets <c>top</c> gets no continuation.
/// </remarks>
public class SearchPagingTests : IDisposable
{
    private const int DocumentCount = 120;

    private readonly RAMDirectory _directory = new();
    private readonly SearchIndex _index;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetIndexSearcher _searcher;

    public SearchPagingTests()
    {
        _index = new SearchIndex
        {
            Name = "paged",
            Fields =
            [
                new SearchField { Name = "Id", Type = "Edm.String", Key = true, Searchable = false, Filterable = true, Sortable = true },
                new SearchField { Name = "Even", Type = "Edm.Boolean", Filterable = true },
            ]
        };

        var factory = new SharedDirectoryFactory(_directory);
        _writerFactory = new LuceneNetIndexWriterFactory(factory);
        _searcher = new LuceneNetIndexSearcher(factory);

        var indexer = new LuceneNetSearchIndexer(_writerFactory, factory);

        indexer.IndexDocuments(_index, Enumerable.Range(0, DocumentCount)
            .Select(i => (IndexDocumentAction)new UploadIndexDocumentAction(new JsonObject
            {
                ["Id"] = i.ToString("D3"),
                ["Even"] = i % 2 == 0,
            }))
            .ToList());
    }

    public void Dispose()
    {
        // Writer before directory: it holds an open handle and commits on dispose.
        _writerFactory.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task WithoutTop_ReturnsTheDefaultPageAndTheSkipForTheNextOne()
    {
        var response = await _searcher.Search(_index, new SearchRequest());

        Assert.Equal(SearchRequest.DefaultTop, response.Results.Count);
        Assert.Equal(SearchRequest.DefaultTop, response.NextPageSkip);
    }

    /// <summary>
    /// The walk a client makes by following each continuation: 50, 50, then the last 20, with
    /// every document returned once.
    /// </summary>
    [Fact]
    public async Task WithoutTop_FollowingEachNextPage_ReturnsEveryDocumentOnce()
    {
        var ids = new List<string>();
        var pageSizes = new List<int>();
        int? skip = 0;

        while (skip is { } nextSkip)
        {
            var response = await _searcher.Search(_index, new SearchRequest { Skip = nextSkip, Orderby = "Id" });

            pageSizes.Add(response.Results.Count);
            ids.AddRange(response.Results.Select(r => r["Id"]!.GetValue<string>()));
            skip = response.NextPageSkip;
        }

        Assert.Equal([50, 50, 20], pageSizes);
        Assert.Equal(Enumerable.Range(0, DocumentCount).Select(i => i.ToString("D3")), ids);
    }

    [Fact]
    public async Task WithoutTop_WhenEveryMatchFitsOnOnePage_OffersNoNextPage()
    {
        // 60 even documents, so skip 10 leaves exactly 50.
        var response = await _searcher.Search(_index, new SearchRequest { Filter = "Even eq true", Skip = 10 });

        Assert.Equal(50, response.Results.Count);
        Assert.Null(response.NextPageSkip);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(SearchRequest.DefaultTop)]
    [InlineData(1000)]
    public async Task WithTop_OffersNoNextPage(int top)
    {
        var response = await _searcher.Search(_index, new SearchRequest { Top = top });

        Assert.Equal(Math.Min(top, DocumentCount), response.Results.Count);
        Assert.Null(response.NextPageSkip);
    }

    private class SharedDirectoryFactory(Lucene.Net.Store.Directory directory) : ILuceneDirectoryFactory, ILuceneIndexReaderFactory
    {
        public Lucene.Net.Store.Directory GetDirectory(string indexName) => directory;
        public void ClearCachedDirectory(string indexName) { }
        public IndexReader GetIndexReader(string indexName) => DirectoryReader.Open(directory);
        public IndexReader RefreshReader(string indexName) => GetIndexReader(indexName);
        public void ClearCachedReader(string indexName) { }
    }
}
