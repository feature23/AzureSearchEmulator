using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public class MergeOrUploadIndexDocumentAction : IndexDocumentAction
{
    public MergeOrUploadIndexDocumentAction(JsonObject item)
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

        var fields = from f in index.Fields
            join v in Item on f.Name equals v.Key
            where v.Value != null
            select f.CreateField(v.Value);

        try
        {
            writer.UpdateDocument(keyTerm, fields);
            return new IndexingResult(keyTerm.Text, true, 200);
        }
        catch (Exception ex)
        {
            return new IndexingResult(keyTerm.Text, $"{ex.GetType().Name}: {ex.Message}", false, 400);
        }
    }
}