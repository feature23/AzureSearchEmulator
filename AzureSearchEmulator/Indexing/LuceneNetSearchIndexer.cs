using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class LuceneNetSearchIndexer(
    ILuceneIndexWriterFactory luceneIndexWriterFactory,
    ILuceneIndexReaderFactory luceneIndexReaderFactory)
    : ISearchIndexer
{
    public IndexDocumentsResult IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions)
    {
        // Shared, long-lived writer — deliberately not disposed here. IndexWriter is
        // internally thread-safe, so concurrent batches against the same index interleave
        // at document granularity instead of contending for the directory's write.lock.
        var writer = luceneIndexWriterFactory.GetIndexWriter(index);

        var key = index.GetKeyField();

        var results = new IndexDocumentsResult();

        var readerLazy = new Lazy<IndexReader>(() => writer.GetReader(true));

        var context = new IndexingContext(index, key, writer, readerLazy);

        try
        {
            foreach (var action in actions)
            {
                var result = action.PerformIndexingAsync(context);
                results.Value.Add(result);
            }
        }
        finally
        {
            // An NRT reader pins segment files, so it must be released even when an action
            // throws — otherwise the leak blocks later deletion of those files on Windows.
            if (readerLazy.IsValueCreated)
            {
                readerLazy.Value.Dispose();
            }
        }

        writer.Commit();

        luceneIndexReaderFactory.RefreshReader(index.Name);

        return results;
    }
}
