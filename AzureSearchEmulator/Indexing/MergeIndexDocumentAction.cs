using System.Text.Json.Nodes;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class MergeIndexDocumentAction : UpsertIndexDocumentActionBase
{
    public MergeIndexDocumentAction(JsonObject item) : base(item)
    {
    }

    protected override void IndexDocument(IndexingContext context, Term keyTerm, IEnumerable<IIndexableField> docFields)
    {
        MergeDocument(context, keyTerm, docFields, false);
    }
}