using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public class LuceneNetSearchIndexer : ISearchIndexer
{
    private readonly ILuceneDirectoryFactory _luceneDirectoryFactory;
    private readonly ILuceneIndexReaderFactory _luceneIndexReaderFactory;

    public LuceneNetSearchIndexer(ILuceneDirectoryFactory luceneDirectoryFactory, ILuceneIndexReaderFactory luceneIndexReaderFactory)
    {
        _luceneDirectoryFactory = luceneDirectoryFactory;
        _luceneIndexReaderFactory = luceneIndexReaderFactory;
    }

    public IndexDocumentsResult IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions)
    {
        var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index.Fields);

        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);

        var directory = _luceneDirectoryFactory.GetDirectory(index.Name);
        using var writer = new IndexWriter(directory, config);

        var key = index.GetKeyField();

        var results = new IndexDocumentsResult();

        // ReSharper disable once AccessToDisposedClosure
        var readerLazy = new Lazy<IndexReader>(() => writer.GetReader(true));

        var context = new IndexingContext(index, key, writer, readerLazy);

        foreach (var action in actions)
        {
            var result = action.PerformIndexingAsync(context);
            results.Value.Add(result);
        }

        if (readerLazy.IsValueCreated)
        {
            var reader = readerLazy.Value;
            reader.Dispose();
        }

        writer.Commit();
        writer.Flush(true, true);

        _luceneIndexReaderFactory.RefreshReader(index.Name);

        return results;
    }
}