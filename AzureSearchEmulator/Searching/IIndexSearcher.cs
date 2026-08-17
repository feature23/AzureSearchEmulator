using System.Text.Json.Nodes;
using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Searching;

public interface IIndexSearcher
{
    /// <summary>
    /// Looks up a single document by key, optionally narrowed to a <c>$select</c> field list.
    /// </summary>
    Task<JsonObject?> GetDoc(SearchIndex index, string docKey, string? select = null);

    Task<int> GetDocCount(SearchIndex index);

    Task<SearchResponse> Search(SearchIndex index, SearchRequest request);
}
