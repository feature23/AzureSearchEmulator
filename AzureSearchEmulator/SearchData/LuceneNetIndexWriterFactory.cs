using System.Collections.Concurrent;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;
using Lucene.Net.Util;

namespace AzureSearchEmulator.SearchData;

/// <summary>
/// Caches one long-lived <see cref="IndexWriter"/> per index.
///
/// Lucene permits only a single IndexWriter per directory and holds a write.lock for that
/// writer's lifetime. Creating a writer per request therefore turns every concurrent
/// indexing request into a race for the lock: the loser fails with
/// LockObtainFailedException, and under sustained concurrency a torn commit corrupts the
/// segment files permanently. Keeping one writer open removes the race entirely —
/// IndexWriter is internally thread-safe and expects concurrent use.
/// </summary>
public class LuceneNetIndexWriterFactory(ILuceneDirectoryFactory luceneDirectoryFactory)
    : ILuceneIndexWriterFactory, IDisposable
{
    // Lazy<T> with ExecutionAndPublication is load-bearing, not a micro-optimization:
    // ConcurrentDictionary.GetOrAdd may invoke its factory concurrently for the same key and
    // discard all but one result. A discarded IndexWriter would never be disposed, leaking
    // write.lock and deadlocking every later write to that index.
    private readonly ConcurrentDictionary<string, Lazy<IndexWriter>> _writers = new();

    public IndexWriter GetIndexWriter(SearchIndex index)
    {
        var indexName = index.Name.ToLowerInvariant();

        var lazy = _writers.GetOrAdd(indexName, _ => new Lazy<IndexWriter>(
            () => CreateWriter(index, indexName),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private IndexWriter CreateWriter(SearchIndex index, string indexName)
    {
        var analyzer = AnalyzerHelper.GetPerFieldIndexAnalyzer(index.Fields);
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);
        var directory = luceneDirectoryFactory.GetDirectory(indexName);

        return new IndexWriter(directory, config);
    }

    public void ClearCachedWriter(string indexName)
    {
        if (!_writers.TryRemove(indexName.ToLowerInvariant(), out var lazy))
        {
            return;
        }

        DisposeWriter(lazy);
    }

    public void Dispose()
    {
        foreach (var lazy in _writers.Values)
        {
            DisposeWriter(lazy);
        }

        _writers.Clear();
        GC.SuppressFinalize(this);
    }

    private static void DisposeWriter(Lazy<IndexWriter> lazy)
    {
        // A writer whose construction threw (IsValueCreated false, or Value rethrowing the
        // cached exception) has nothing to release; evicting the entry is the whole job.
        if (!lazy.IsValueCreated)
        {
            return;
        }

        try
        {
            lazy.Value.Dispose();
        }
        catch (Exception)
        {
            // Dispose() commits pending changes and can throw if the underlying directory
            // was already removed. The writer is being discarded either way, and letting
            // this escape would fail an otherwise-successful index delete.
        }
    }
}
