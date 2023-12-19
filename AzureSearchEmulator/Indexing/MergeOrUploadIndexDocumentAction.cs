using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class MergeOrUploadIndexDocumentAction(JsonObject item) : UpsertIndexDocumentActionBase(item)
{
    protected override void IndexDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields)
        => MergeDocument(context, keyTerm, docFields, true);
}
