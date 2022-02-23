using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

public interface ISearchIndexer
{
    IndexDocumentsResult IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions);
}