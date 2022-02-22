using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Indexing;

public class MergeOrUploadIndexDocumentAction : IndexDocumentAction
{
    public MergeOrUploadIndexDocumentAction(JsonObject item)
    {
        Item = item;
    }

    public JsonObject Item { get; }

    public override Task PerformIndexingAsync()
    {
        throw new NotImplementedException();
    }
}