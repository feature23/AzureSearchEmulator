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
    ISearchIndexer searchIndexer,
    ILogger<DocumentIndexingController> logger)
    : ODataController
{
    /// <summary>
    /// Cap on the batch body written to the log. Azure Search allows batches up to
    /// 1000 documents / 16 MB; writing one whole to the log per request costs more than
    /// it reveals, and the leading bytes are enough to see whether the payload carried
    /// the fields the caller expected.
    /// </summary>
    private const int MaxLoggedBodyLength = 4096;

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

        // Batch body, logged BEFORE deserialization so even a malformed payload is
        // visible. At Debug so it is opt-in: the previous unconditional Console.WriteLine
        // wrote the whole body — up to 16 MB — synchronously on the request thread under
        // the process-wide console lock, which serialized concurrent indexing.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("[INDEX {IndexKey}] body: {Body}", indexKey, Truncate(json));
        }

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

        // Per-item outcomes — the Azure SDK's IndexDocuments does NOT throw on per-item
        // failures by default, so a rejected merge is invisible to a caller that does not
        // inspect the batch response. Failures log at Warning so they surface without
        // needing Debug enabled; the all-succeeded case stays at Debug.
        var failed = result.Value.Where(i => !i.Status).ToList();
        var succeeded = result.Value.Count - failed.Count;

        if (failed.Count > 0)
        {
            logger.LogWarning("[INDEX {IndexKey}] {Succeeded}/{Total} ok — {Failures}",
                indexKey, succeeded, actions.Count,
                string.Join(", ", failed.Select(i => $"{i.Key}:{i.StatusCode} FAILED({i.ErrorMessage})")));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("[INDEX {IndexKey}] {Succeeded}/{Total} ok", indexKey, succeeded, actions.Count);
        }

        return StatusCode(failed.Count > 0 ? 207 : 200, result);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxLoggedBodyLength
            ? value
            : $"{value[..MaxLoggedBodyLength]}… [truncated, {value.Length} chars total]";
}
