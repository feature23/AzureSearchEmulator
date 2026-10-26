using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class MergeIndexDocumentAction(JsonObject item) : UpsertIndexDocumentActionBase(item)
{
    protected override void IndexDocument(IndexingContext context, Term keyTerm)
        => MergeDocument(context, keyTerm, false);
}
