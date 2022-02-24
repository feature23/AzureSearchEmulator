using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Indexing;

public class DeleteIndexDocumentAction : IndexDocumentAction
{
    public DeleteIndexDocumentAction(JsonObject item)
        : base(item)
    {
    }
    
    public override IndexingResult PerformIndexingAsync(IndexingContext context)
    {
        var keyTerm = GetKeyTerm(context.Key);

        context.Writer.DeleteDocuments(keyTerm);

        return new IndexingResult(keyTerm.Text, true, 200);
    }
}