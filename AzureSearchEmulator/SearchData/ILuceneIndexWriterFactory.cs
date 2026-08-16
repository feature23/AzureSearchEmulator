using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.SearchData;

public interface ILuceneIndexWriterFactory
{
    /// <summary>
    /// Gets the long-lived <see cref="IndexWriter"/> for an index, creating it on first use.
    /// The returned writer is shared and must NOT be disposed by callers — it stays open for
    /// the lifetime of the process (or until <see cref="ClearCachedWriter"/> evicts it).
    /// <see cref="IndexWriter"/> is internally thread-safe, so concurrent callers may use the
    /// same instance simultaneously.
    /// </summary>
    IndexWriter GetIndexWriter(SearchIndex index);

    /// <summary>
    /// Disposes and evicts the cached writer, releasing the directory's write.lock and all
    /// open segment file handles. Must be called before an index's directory is deleted from
    /// disk or its schema is changed, otherwise a stale writer keeps writing to the old
    /// (possibly unlinked) segments with a stale analyzer.
    /// </summary>
    void ClearCachedWriter(string indexName);
}
