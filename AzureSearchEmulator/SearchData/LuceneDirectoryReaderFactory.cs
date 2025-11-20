using System.Collections.Concurrent;
using Lucene.Net.Index;

namespace AzureSearchEmulator.SearchData;

public class LuceneDirectoryReaderFactory(ILuceneDirectoryFactory luceneDirectoryFactory) : ILuceneIndexReaderFactory
{
    private readonly ConcurrentDictionary<string, IndexReader> _indexReaders = new();

    public IndexReader GetIndexReader(string indexName)
    {
        indexName = indexName.ToLowerInvariant();

        if (_indexReaders.TryGetValue(indexName, out var reader))
        {
            return reader;
        }

        reader = RefreshReader(indexName);

        return reader;
    }

    public IndexReader RefreshReader(string indexName)
    {
        var directory = luceneDirectoryFactory.GetDirectory(indexName);

        var reader = DirectoryReader.Open(directory);

        _indexReaders[indexName] = reader;

        return reader;
    }

    public void ClearCachedReader(string indexName)
    {
        indexName = indexName.ToLowerInvariant();

        if (_indexReaders.TryRemove(indexName, out var reader))
        {
            reader.Dispose();
        }
    }
}
