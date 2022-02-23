using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Searching;

public class SearchResponse
{
    public int? Count { get; set; }

    public IList<JsonObject> Results { get; set; } = new List<JsonObject>();
}