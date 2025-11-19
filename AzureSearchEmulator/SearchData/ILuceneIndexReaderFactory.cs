using Lucene.Net.Index;

namespace AzureSearchEmulator.SearchData;

public interface ILuceneIndexReaderFactory
{
    IndexReader GetIndexReader(string indexName);

    IndexReader RefreshReader(string indexName);

    void ClearCachedReader(string indexName);
}