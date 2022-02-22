using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Indexing;

public interface ISearchIndexer
{
    Task<IndexDocumentsResult> IndexDocuments(SearchIndex index, IList<IndexDocumentAction> actions, CancellationToken cancellationToken = default);
}