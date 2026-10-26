using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class UploadIndexDocumentAction(JsonObject item) : UpsertIndexDocumentActionBase(item)
{
    protected override void IndexDocument(IndexingContext context, Term keyTerm)
        => context.Writer.UpdateDocument(keyTerm, GetDocFields(context.Index, Item));
}
