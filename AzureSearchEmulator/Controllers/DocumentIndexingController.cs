using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class DocumentIndexingController : ODataController
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ISearchIndexRepository _searchIndexRepository;
    private readonly ISearchIndexer _searchIndexer;

    public DocumentIndexingController(JsonSerializerOptions jsonSerializerOptions,
        ISearchIndexRepository searchIndexRepository,
        ISearchIndexer searchIndexer)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
        _searchIndexRepository = searchIndexRepository;
        _searchIndexer = searchIndexer;
    }

    [HttpPost]
    [Route("indexes('{indexKey}')/docs/search.index")]
    [Route("indexes/{indexKey}/docs/search.index")]
    public async Task<IActionResult> IndexDocuments(string indexKey)
    {
        var index = await _searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound();
        }

        using var sr = new StreamReader(Request.Body);
        var json = await sr.ReadToEndAsync();
        var batch = JsonSerializer.Deserialize<IndexDocumentsBatch>(json, _jsonSerializerOptions);

        if (batch == null)
        {
            return BadRequest();
        }

        int itemIndex = 0;
        var actions = new List<IndexDocumentAction>();

        foreach (var item in batch.Value)
        {
            var actionNode = item["@search.action"];

            if (actionNode == null)
            {
                ModelState.AddModelError($"value[{itemIndex}]", "Batch item missing @search.action property");
                return BadRequest(ModelState);
            }

            var action = actionNode.GetValue<string>();
            
            actions.Add(action switch {
                "mergeOrUpload" => new MergeOrUploadIndexDocumentAction(item),
                "delete" => new DeleteIndexDocumentAction(item),
                _ => throw new NotImplementedException($"Emulator does not yet support '{action}' actions")
            });

            itemIndex++;
        }

        var result = _searchIndexer.IndexDocuments(index, actions);

        return StatusCode(result.Value.Any(i => !i.Status) ? 207 : 200, result);
    }
}