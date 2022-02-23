using System.Collections.Concurrent;
using Lucene.Net.Index;

namespace AzureSearchEmulator.SearchData;

public class LuceneDirectoryReaderFactory : ILuceneIndexReaderFactory
{
    private readonly ILuceneDirectoryFactory _luceneDirectoryFactory;

    private readonly IDictionary<string, IndexReader> _indexReaders = new ConcurrentDictionary<string, IndexReader>();

    public LuceneDirectoryReaderFactory(ILuceneDirectoryFactory luceneDirectoryFactory)
    {
        _luceneDirectoryFactory = luceneDirectoryFactory;
    }

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
        var directory = _luceneDirectoryFactory.GetDirectory(indexName);

        var reader = DirectoryReader.Open(directory);

        _indexReaders[indexName] = reader;

        return reader;
    }
}