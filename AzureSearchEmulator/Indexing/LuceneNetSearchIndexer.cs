using System.Collections.Concurrent;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.SearchData;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.Indexing;

public class LuceneNetSearchIndexer(
    ILuceneDirectoryFactory luceneDirectoryFactory,
    ILuceneIndexReaderFactory luceneIndexReaderFactory)
    : ISearchIndexer
{
    // One writer gate per index. Lucene allows only a single IndexWriter per
    // directory; this class creates a fresh writer per request, so two
    // concurrent indexing requests against the same index would race — the
    // loser either fails with LockObtainFailedException or, worse, observes
    // a torn commit and permanently corrupts the segment files
    // (FileNotFoundException: .../_N.si on every subsequent write until the
    // index directory is deleted). Serializing IndexDocuments per index name
    // removes both failure modes; requests against different indexes still
    // run in parallel.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> IndexWriteGates = new();

    public IndexDocumentsResult IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions)
    {
        var gate = IndexWriteGates.GetOrAdd(index.Name, static _ => new SemaphoreSlim(1, 1));
        gate.Wait();

        try
        {
            var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index.Fields);

            var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);

            var directory = luceneDirectoryFactory.GetDirectory(index.Name);
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

            luceneIndexReaderFactory.RefreshReader(index.Name);

            return results;
        }
        finally
        {
            gate.Release();
        }
    }
}
