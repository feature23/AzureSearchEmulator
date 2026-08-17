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
    public async Task<IActionResult> GetDocument(string indexKey, string key,
        [FromQuery(Name = "$select")] string? select)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');
        key = key.Trim('\'');

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        var doc = await indexSearcher.GetDoc(index, key, select);

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
        // Azure's GET syntax names this one in the singular and repeats it, unlike the POST
        // body's "scoringParameters" array, so it needs its own binding to be seen at all —
        // and an unsupported parameter that binds to nothing would be silently ignored, which
        // is precisely what issue #39 is about.
        [FromQuery(Name = "scoringParameter")] IList<string>? scoringParameter,
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
        searchRequest.ScoringParameters ??= scoringParameter;

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

        // Checked before the index lookup so that an unsupported parameter is reported as
        // itself, rather than being masked by a 404 for an index the caller has not created
        // yet. See UnsupportedSearchParameters for why these are refused instead of ignored.
        if (UnsupportedSearchParameters.GetRejectionMessage(request) is { } rejection)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, rejection);
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

        if (response.Coverage != null)
        {
            oDataResponse["@search.coverage"] = JsonValue.Create(response.Coverage);
        }

        if (response.Facets != null)
        {
            oDataResponse["@search.facets"] = BuildFacets(response.Facets);
        }

        oDataResponse["value"] = new JsonArray(response.Results.OfType<JsonNode>().ToArray());

        return Ok(oDataResponse);
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs/suggest")]
    [Route("indexes({indexKey})/docs/suggest")]
    public async Task<IActionResult> SuggestGet(string indexKey,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$orderby")] string? orderby,
        [FromQuery(Name = "$select")] string? select,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery] SuggestRequest suggestRequest)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (top != null)
        {
            suggestRequest.Top = top.Value;
        }

        suggestRequest.Filter ??= filter;
        suggestRequest.Orderby ??= orderby;
        suggestRequest.Select ??= select;

        return await SuggestPost(indexKey, suggestRequest);
    }

    [HttpPost]
    [Route("indexes/{indexKey}/docs/suggest")]
    [Route("indexes({indexKey})/docs/suggest")]
    [Route("indexes({indexKey})/docs/search.post.suggest")]
    public async Task<IActionResult> SuggestPost(string indexKey, [FromBody] SuggestRequest request)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        if (SuggesterSupport.ValidateTop(request.Top) is { } topError)
        {
            ModelState.AddModelError(nameof(request.Top), topError);
            return BadRequest(ModelState);
        }

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        SuggestResponse response;

        try
        {
            response = await indexSearcher.Suggest(index, request);
        }
        catch (InvalidOperationException ex)
        {
            // A missing or misnamed suggester is a fault in the request, not in the emulator,
            // so it is reported as a 400 rather than escaping as a 500.
            return BadRequest(ex.Message);
        }

        var oDataResponse = new JsonObject();

        if (response.Coverage != null)
        {
            oDataResponse["@search.coverage"] = JsonValue.Create(response.Coverage);
        }

        oDataResponse["value"] = new JsonArray(response.Results.OfType<JsonNode>().ToArray());

        return Ok(oDataResponse);
    }

    [HttpGet]
    [Route("indexes/{indexKey}/docs/autocomplete")]
    [Route("indexes({indexKey})/docs/autocomplete")]
    public async Task<IActionResult> AutocompleteGet(string indexKey,
        [FromQuery(Name = "$filter")] string? filter,
        [FromQuery(Name = "$top")] int? top,
        [FromQuery] AutocompleteRequest autocompleteRequest)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (top != null)
        {
            autocompleteRequest.Top = top.Value;
        }

        autocompleteRequest.Filter ??= filter;

        return await AutocompletePost(indexKey, autocompleteRequest);
    }

    [HttpPost]
    [Route("indexes/{indexKey}/docs/autocomplete")]
    [Route("indexes({indexKey})/docs/autocomplete")]
    [Route("indexes({indexKey})/docs/search.post.autocomplete")]
    public async Task<IActionResult> AutocompletePost(string indexKey, [FromBody] AutocompleteRequest request)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        if (SuggesterSupport.ValidateTop(request.Top) is { } topError)
        {
            ModelState.AddModelError(nameof(request.Top), topError);
            return BadRequest(ModelState);
        }

        if (!AutocompleteModes.IsValid(request.AutocompleteMode))
        {
            ModelState.AddModelError(nameof(request.AutocompleteMode),
                $"autocompleteMode must be one of {AutocompleteModes.OneTerm}, " +
                $"{AutocompleteModes.TwoTerms}, or {AutocompleteModes.OneTermWithContext}.");
            return BadRequest(ModelState);
        }

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound($"The specified index does not exist. Index Key: {indexKey}");
        }

        AutocompleteResponse response;

        try
        {
            response = await indexSearcher.Autocomplete(index, request);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        var oDataResponse = new JsonObject();

        if (response.Coverage != null)
        {
            oDataResponse["@search.coverage"] = JsonValue.Create(response.Coverage);
        }

        var items = new JsonArray();

        foreach (var item in response.Results)
        {
            items.Add(new JsonObject
            {
                ["text"] = JsonValue.Create(item.Text),
                ["queryPlusText"] = JsonValue.Create(item.QueryPlusText),
            });
        }

        oDataResponse["value"] = items;

        return Ok(oDataResponse);
    }

    /// <summary>
    /// Renders the counted facets as the <c>@search.facets</c> object.
    /// </summary>
    /// <remarks>
    /// A range bucket carries only the bounds it actually has: Azure Search omits <c>from</c>
    /// on the first bucket and <c>to</c> on the last rather than sending them as null, and
    /// the SDK relies on that to tell an open-ended bucket from a bounded one.
    /// </remarks>
    private static JsonObject BuildFacets(
        IReadOnlyDictionary<string, IReadOnlyList<FacetBucket>> facets)
    {
        var result = new JsonObject();

        foreach (var (name, buckets) in facets)
        {
            var array = new JsonArray();

            foreach (var bucket in buckets)
            {
                var item = new JsonObject();

                if (bucket.From != null)
                {
                    item["from"] = JsonValue.Create(bucket.From);
                }

                if (bucket.To != null)
                {
                    item["to"] = JsonValue.Create(bucket.To);
                }

                if (bucket.Value != null)
                {
                    item["value"] = JsonValue.Create(bucket.Value);
                }

                item["count"] = JsonValue.Create(bucket.Count);

                array.Add(item);
            }

            result[name] = array;
        }

        return result;
    }
}
