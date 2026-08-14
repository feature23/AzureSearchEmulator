using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class DocumentIndexingController(
    JsonSerializerOptions jsonSerializerOptions,
    ISearchIndexRepository searchIndexRepository,
    ISearchIndexer searchIndexer)
    : ODataController
{
    [HttpPost]
    [Route("indexes({indexKey})/docs/search.index")]
    [Route("indexes/{indexKey}/docs/search.index")]
    public async Task<IActionResult> IndexDocuments(string indexKey)
    {
        // Strip quotes that may be captured from OData-style URLs
        indexKey = indexKey.Trim('\'');

        var index = await searchIndexRepository.Get(indexKey);

        if (index == null)
        {
            return NotFound();
        }

        using var sr = new StreamReader(Request.Body);
        var json = await sr.ReadToEndAsync();

        // Full batch body — logged BEFORE deserialization so even a malformed
        // payload is visible. Devbox visibility beats log volume here: a merge
        // whose body carried the wrong fields is otherwise undiagnosable.
        Console.WriteLine($"[INDEX {indexKey}] body: {json}");

        var batch = JsonSerializer.Deserialize<IndexDocumentsBatch>(json, jsonSerializerOptions);

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
                "upload" => new UploadIndexDocumentAction(item),
                "merge" => new MergeIndexDocumentAction(item),
                "mergeOrUpload" => new MergeOrUploadIndexDocumentAction(item),
                "delete" => new DeleteIndexDocumentAction(item),
                _ => throw new NotImplementedException($"Emulator does not yet support '{action}' actions")
            });

            itemIndex++;
        }

        var result = searchIndexer.IndexDocuments(index, actions);

        // Per-item outcomes, keyed — the Azure SDK's IndexDocuments does NOT
        // throw on per-item failures by default, so a rejected merge is
        // invisible to the caller unless it checks the batch response. This
        // log line is the only place a dropped write is guaranteed to surface.
        var summary = string.Join(", ", result.Value.Select(i =>
            i.Status ? $"{i.Key}:{i.StatusCode}" : $"{i.Key}:{i.StatusCode} FAILED({i.ErrorMessage})"));
        Console.WriteLine($"[INDEX {indexKey}] {result.Value.Count(i => i.Status)}/{actions.Count} ok — {summary}");

        return StatusCode(result.Value.Any(i => !i.Status) ? 207 : 200, result);
    }
}
