using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;
using Lucene.Net.Index;

namespace AzureSearchEmulator.Indexing;

public abstract class IndexDocumentAction(JsonObject item)
{
    public JsonObject Item { get; } = item;

    protected Term GetKeyTerm(SearchField key)
    {
        var keyNode = Item[key.Name];

        if (keyNode == null)
        {
            throw new InvalidOperationException($"Key value for key '{key.Name}' not found");
        }

        return new Term(key.Name, keyNode.GetValue<string>());
    }

    public abstract IndexingResult PerformIndexingAsync(IndexingContext context);
}
