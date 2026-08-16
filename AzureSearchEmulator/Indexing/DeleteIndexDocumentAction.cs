using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class DeleteIndexDocumentAction(JsonObject item) : IndexDocumentAction(item)
{
    public override IndexingResult PerformIndexingAsync(IndexingContext context)
    {
        // See UpsertIndexDocumentActionBase: an uncaught throw here escapes IndexDocuments
        // and 500s the entire batch instead of failing this one item.
        Term keyTerm;

        try
        {
            keyTerm = GetKeyTerm(context.Key);
        }
        catch (Exception ex)
        {
            return new IndexingResult(string.Empty, $"{ex.GetType().Name}: {ex.Message}", false, 400);
        }

        try
        {
            context.Writer.DeleteDocuments(keyTerm);
            return new IndexingResult(keyTerm.Text, true, 200);
        }
        catch (Exception ex)
        {
            return new IndexingResult(keyTerm.Text, $"{ex.GetType().Name}: {ex.Message}", false, 400);
        }
    }
}
