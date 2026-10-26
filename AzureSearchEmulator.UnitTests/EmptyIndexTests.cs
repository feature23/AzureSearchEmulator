using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Lucene.Net.Store;
using Xunit;
using Directory = Lucene.Net.Store.Directory;

namespace AzureSearchEmulator.UnitTests;

/// <summary>
/// Covers reading an index that exists but has never had a document written to it, which
/// Azure answers with an empty result rather than an error.
/// </summary>
/// <remarks>
/// Uses the real <see cref="LuceneDirectoryReaderFactory"/> over an on-disk directory, because
/// the failure comes from opening a reader on a directory Lucene has never written. An
/// in-memory directory is always readable, so a test built on one would pass without the fix.
/// </remarks>
public sealed class EmptyIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "azs-empty-index-" + Guid.NewGuid().ToString("N"));
    private readonly SearchIndex _index = LuceneTestHelper.CreateSuggesterProductIndex();
    private readonly LuceneDirectoryReaderFactory _readerFactory;
    private readonly LuceneNetIndexWriterFactory _writerFactory;
    private readonly LuceneNetSearchIndexer _indexer;
    private readonly LuceneNetIndexSearcher _searcher;

    public EmptyIndexTests()
    {
        var directoryFactory = new FixedPathDirectoryFactory(_path);

        _readerFactory = new LuceneDirectoryReaderFactory(directoryFactory);
        _writerFactory = new LuceneNetIndexWriterFactory(directoryFactory);
        _indexer = new LuceneNetSearchIndexer(_writerFactory, _readerFactory);
        _searcher = new LuceneNetIndexSearcher(_readerFactory);
    }

    public void Dispose()
    {
        // Reader and writer before the folder: both hold open handles.
        _readerFactory.ClearCachedReader(_index.Name);
        _writerFactory.Dispose();

        if (System.IO.Directory.Exists(_path))
        {
            System.IO.Directory.Delete(_path, recursive: true);
        }
    }

    [Fact]
    public async Task Search_OnAnIndexWithNoDocuments_ReturnsNoResults()
    {
        var response = await _searcher.Search(_index, new SearchRequest { Search = "*", Count = true });

        Assert.Empty(response.Results);
        Assert.Equal(0, response.Count);
    }

    [Fact]
    public async Task Search_ForText_OnAnIndexWithNoDocuments_ReturnsNoResults()
    {
        var response = await _searcher.Search(_index, new SearchRequest { Search = "laptop", Count = true });

        Assert.Empty(response.Results);
        Assert.Equal(0, response.Count);
    }

    [Fact]
    public async Task GetDocCount_OnAnIndexWithNoDocuments_ReturnsZero()
    {
        Assert.Equal(0, await _searcher.GetDocCount(_index));
    }

    [Fact]
    public async Task GetDoc_OnAnIndexWithNoDocuments_ReturnsNull()
    {
        Assert.Null(await _searcher.GetDoc(_index, "1"));
    }

    [Fact]
    public async Task Suggest_OnAnIndexWithNoDocuments_ReturnsNoResults()
    {
        var response = await _searcher.Suggest(_index, new SuggestRequest { Search = "lap", SuggesterName = "sg" });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task Autocomplete_OnAnIndexWithNoDocuments_ReturnsNoResults()
    {
        var response = await _searcher.Autocomplete(_index,
            new AutocompleteRequest { Search = "lap", SuggesterName = "sg" });

        Assert.Empty(response.Results);
    }

    /// <summary>
    /// A volume can hold a folder that Lucene created but never committed to.
    /// </summary>
    [Fact]
    public async Task Search_WhenTheIndexFolderExistsButIsEmpty_ReturnsNoResults()
    {
        System.IO.Directory.CreateDirectory(_path);

        var response = await _searcher.Search(_index, new SearchRequest { Search = "*", Count = true });

        Assert.Empty(response.Results);
        Assert.Equal(0, response.Count);
    }

    /// <summary>
    /// The empty answer must not be kept: once a document is written, reads see it.
    /// </summary>
    [Fact]
    public async Task Reads_AfterTheFirstDocumentIsWritten_SeeIt()
    {
        // Read first, so the empty answer is the one a stale cache would keep serving.
        Assert.Empty((await _searcher.Search(_index, new SearchRequest { Search = "*" })).Results);
        Assert.Equal(0, await _searcher.GetDocCount(_index));

        _indexer.IndexDocuments(_index,
        [
            new UploadIndexDocumentAction(new JsonObject
            {
                ["Id"] = "1",
                ["Name"] = "Laptop Pro 15",
                ["Description"] = "High-performance laptop",
            })
        ]);

        var response = await _searcher.Search(_index, new SearchRequest { Search = "laptop", Count = true });

        Assert.Equal(1, response.Count);
        Assert.Equal("1", Assert.Single(response.Results)["Id"]!.GetValue<string>());
        Assert.Equal(1, await _searcher.GetDocCount(_index));
        Assert.NotNull(await _searcher.GetDoc(_index, "1"));
    }

    private sealed class FixedPathDirectoryFactory(string path) : ILuceneDirectoryFactory
    {
        private Directory? _directory;

        public Directory GetDirectory(string indexName) => _directory ??= new SimpleFSDirectory(path);

        public void ClearCachedDirectory(string indexName)
        {
            _directory?.Dispose();
            _directory = null;
        }
    }
}
