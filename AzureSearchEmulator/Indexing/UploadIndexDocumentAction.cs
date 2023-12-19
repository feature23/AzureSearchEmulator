using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class UploadIndexDocumentAction(JsonObject item) : UpsertIndexDocumentActionBase(item)
{
    protected override void IndexDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields)
        => context.Writer.UpdateDocument(keyTerm, docFields);
}
