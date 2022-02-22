using Directory = Lucene.Net.Store.Directory;

namespace AzureSearchEmulator.SearchData;

public interface ILuceneDirectoryFactory
{
    Directory GetDirectory(string indexName);
}