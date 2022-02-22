using Lucene.Net.Index;

namespace AzureSearchEmulator.SearchData;

public interface ILuceneIndexReaderFactory
{
    IndexReader GetIndexReader(string indexName);
}