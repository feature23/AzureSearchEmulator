using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public abstract class IndexDocumentAction
{
    public abstract IndexingResult PerformIndexingAsync(SearchIndex index, SearchField key, IndexWriter writer);
}