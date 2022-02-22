namespace AzureSearchEmulator.Indexing;

public abstract class IndexDocumentAction
{
    public abstract Task PerformIndexingAsync();
}