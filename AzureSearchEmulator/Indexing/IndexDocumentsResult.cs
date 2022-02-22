namespace AzureSearchEmulator.Indexing;

public class IndexDocumentsResult
{
    public IList<IndexingResult> Value { get; set; } = new List<IndexingResult>();
}