using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class DeleteIndexDocumentAction : IndexDocumentAction
{
    public DeleteIndexDocumentAction(JsonObject item)
    {
        Item = item;
    }

    public JsonObject Item { get; }
    
    public override IndexingResult PerformIndexingAsync(SearchIndex index, SearchField key, IndexWriter writer)
    {
        var keyNode = Item[key.Name];

        if (keyNode == null)
        {
            throw new InvalidOperationException($"Key value for key '{key.Name}' not found");
        }

        var keyTerm = new Term(key.Name, keyNode.GetValue<string>());

        writer.DeleteDocuments(keyTerm);

        return new IndexingResult(keyTerm.Text, true, 200);
    }
}