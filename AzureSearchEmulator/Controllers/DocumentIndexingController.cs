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
            logger.LogDebug("[INDEX {IndexKey}] body: {Body}", Sanitize(indexKey), SanitizeAndTruncate(json));
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
                Sanitize(indexKey), succeeded, actions.Count,
                string.Join(", ", failed.Select(i =>
                    $"{Sanitize(i.Key)}:{i.StatusCode} FAILED({Sanitize(i.ErrorMessage)})")));
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("[INDEX {IndexKey}] {Succeeded}/{Total} ok", Sanitize(indexKey), succeeded, actions.Count);
        }

        return StatusCode(failed.Count > 0 ? 207 : 200, result);
    }

    /// <summary>
    /// Strips CR/LF and other control characters from a value before it reaches the log.
    /// Every value logged here — the index key, the request body, per-item error messages —
    /// is attacker-controlled, and an embedded newline lets a caller forge whole log lines.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = char.IsControl(c) ? ' ' : c;
            }
        });
    }

    private static string SanitizeAndTruncate(string value)
    {
        var sanitized = Sanitize(value);

        if (sanitized.Length <= MaxLoggedBodyLength)
        {
            return sanitized;
        }

        // Back off one char if the cut would land between a surrogate pair, so the log
        // never carries a lone surrogate.
        var cut = MaxLoggedBodyLength;

        if (char.IsHighSurrogate(sanitized[cut - 1]))
        {
            cut--;
        }

        return $"{sanitized[..cut]}… [truncated, {sanitized.Length} chars total]";
    }
}
