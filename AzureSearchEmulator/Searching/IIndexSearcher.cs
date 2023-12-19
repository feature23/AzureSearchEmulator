using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

public interface IIndexSearcher
{
    Task<JsonObject?> GetDoc(SearchIndex index, string docKey);

    Task<int> GetDocCount(SearchIndex index);

    Task<SearchResponse> Search(SearchIndex index, SearchRequest request);
}
