using System.Text.Json.Nodes;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.Searching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class DocumentSearchingController(
    IIndexSearcher indexSearcher,
    ISearchIndexRepository searchIndexRepository)
    : ODataController
{
    [HttpGet]
    [Route("indexes/{indexKey}/docs/$count")]
    [Route("indexes({indexKey})/docs/$count")]
    public async Task<IActionResult> GetDocumentCount(string indexKey)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        var count = await indexSearcher.GetDocCount(index);

        return Ok(count);
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs/{key}")]
    [Route("indexes({indexKey})/docs({key})")]
    public async Task<IActionResult> GetDocument(string indexKey, string key)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');
        key = key.Trim('\'');

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        var doc = await indexSearcher.GetDoc(index, key);

        if (doc == null)
        {
            return NotFound($"The specified document does not exist. Key: {key}");
        }

        return Ok(doc);
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs")]
    [Route("indexes({indexKey})/docs")]
    public async Task<IActionResult> SearchGet(string indexKey,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$count")] bool? count,
        [FromQuery(Name = "$orderby")] string? orderby,
        [FromQuery(Name = "$select")] string? select,
        [FromQuery(Name = "$skip")] int? skip,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery(Name = "facet")] IList<string>? facet,
        [FromQuery] SearchRequest searchRequest)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (count != null)
        {
            searchRequest.Count = count.Value;
        }

        if (skip != null)
        {
            searchRequest.Skip = skip.Value;
        }

        if (top != null)
        {
            searchRequest.Top = top.Value;
        }

        searchRequest.Filter ??= filter;
        searchRequest.Orderby ??= orderby;
        searchRequest.Select ??= select;
        searchRequest.Facets ??= facet;

        return await SearchPost(indexKey, searchRequest);
    }

    [HttpPost]
    [Route("indexes/{indexKey}/docs/search")]
    [Route("indexes({indexKey})/docs/search")]
    [Route("indexes({indexKey})/docs/search.post.search")]
    public async Task<IActionResult> SearchPost(string indexKey, [FromBody] SearchRequest request)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        if (request.Top is > 1000 or < 0)
        {
            ModelState.AddModelError(nameof(request.Top), "Page size must be between 0 and 1000");
        }

        if (request.Skip is > 100_000 or < 0)
        {
            ModelState.AddModelError(nameof(request.Skip), "Skip must be between 0 and 100,000");
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        var response = await indexSearcher.Search(index, request);

        var oDataResponse = new JsonObject();

        if (response.Count != null)
        {
            oDataResponse["@odata.count"] = JsonValue.Create(response.Count);
        }

        oDataResponse["value"] = new JsonArray(response.Results.OfType<JsonNode>().ToArray());

        return Ok(oDataResponse);
    }
}
