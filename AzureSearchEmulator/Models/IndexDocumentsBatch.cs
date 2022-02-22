using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Models;

public class IndexDocumentsBatch
{
    public IList<JsonObject> Value { get; set; } = new List<JsonObject>();
}