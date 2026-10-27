using System.Text.Json;
using System.Text.Json.Nodes;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.SearchData;
using AzureSearchEmulator.Searching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class IndexesController(
    JsonSerializerOptions jsonSerializerOptions,
    ISearchIndexRepository searchIndexRepository,
    ISynonymMapRepository synonymMapRepository,
    IAliasRepository aliasRepository,
    IIndexResolver indexResolver,
    ILuceneDirectoryFactory luceneDirectoryFactory,
    ILuceneIndexReaderFactory luceneIndexReaderFactory,
    ILuceneIndexWriterFactory luceneIndexWriterFactory,
    IIndexSearcher indexSearcher)
    : ODataController
{
    /// <remarks>
    /// Serialized the same way as the single-index responses (see <see cref="IndexJson"/>)
    /// rather than through OData. OData's serializer left out the properties held in
    /// <see cref="SearchIndex.AdditionalProperties"/>, such as semantic configurations, and wrote
    /// a scoring profile's text weights as an array of empty objects, which
    /// <c>SearchIndexClient.GetIndexesAsync</c> cannot read — so listing any index with a
    /// weighted scoring profile failed in the SDK (found building the index pages, issue #91).
    /// <para>
    /// <c>$select</c> is the only query option Azure documents for this listing, and it names
    /// top-level properties, so it is applied here by hand. <c>SearchIndexClient.GetIndexNamesAsync</c>
    /// sends <c>$select=name</c>.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("indexes")]
    public async Task<IActionResult> List([FromQuery(Name = "$select")] string? select)
    {
        var properties = string.IsNullOrWhiteSpace(select) || select.Trim() == "*"
            ? null
            : select.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

        var value = new JsonArray();

        await foreach (var index in searchIndexRepository.GetAll())
        {
            var node = JsonSerializer.SerializeToNode(index, jsonSerializerOptions)!.AsObject();

            if (properties is not null)
            {
                foreach (var name in node.Select(i => i.Key).Where(i => !properties.Contains(i)).ToList())
                {
                    node.Remove(name);
                }
            }

            value.Add(node);
        }

        var response = new JsonObject
        {
            ["@odata.context"] = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/$metadata#indexes",
            ["value"] = value
        };

        return new ContentResult
        {
            Content = response.ToJsonString(jsonSerializerOptions),
            ContentType = "application/json",
            StatusCode = StatusCodes.Status200OK
        };
    }

    /// <remarks>
    /// Resolves an alias, as Azure does for getting and updating a definition. The response is
    /// the target's definition, under the target's name.
    /// </remarks>
    [HttpGet]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        // Strip quotes that may be captured from OData-style URLs
        key = key.Trim('\'');

        var index = await indexResolver.Get(key);

        if (index == null)
        {
            return NotFound();
        }

        return IndexJson(index);
    }

    [HttpPost]
    [Route("indexes")]
    public async Task<IActionResult> Post([FromBody] SearchIndex? index)
    {
        if (index == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (ValidateComplexFields(index) is { } complexError)
        {
            return BadRequest(complexError);
        }

        if (ScoringProfileValidator.FindInvalidProfile(index) is { } scoringError)
        {
            return BadRequest(scoringError);
        }

        if (VectorSearchValidator.FindInvalidVectorSearch(index) is { } vectorError)
        {
            return BadRequest(vectorError);
        }

        if (AnalyzerValidator.FindInvalidAnalyzer(index) is { } analyzerError)
        {
            return BadRequest(analyzerError);
        }

        if (NormalizerValidator.FindInvalidNormalizer(index) is { } normalizerError)
        {
            return BadRequest(normalizerError);
        }

        if (await FindInvalidSynonymMapReference(index) is { } synonymMapError)
        {
            return BadRequest(synonymMapError);
        }

        if (await aliasRepository.Get(index.Name) != null)
        {
            return Conflict($"Index name '{index.Name}' is already used by an alias.");
        }

        try
        {
            await searchIndexRepository.Create(index);
        }
        catch (SearchIndexExistsException ex)
        {
            // The message names the index; a bare Conflict() left the caller with only the
            // status code to work from.
            return Conflict(ex.Message);
        }

        return IndexJson(index, StatusCodes.Status201Created);
    }

    /// <remarks>
    /// Addressed by an alias, this updates the alias's target, never creates an index under the
    /// alias's name. The body may then carry either name; the target's is the one stored.
    /// </remarks>
    [HttpPut]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] SearchIndex? index)
    {
        // Strip quotes that may be captured from OData-style URLs
        key = key.Trim('\'');

        if (index == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var existing = await indexResolver.Get(key);

        if (!string.Equals(index.Name, key, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(index.Name, existing?.Name, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(index.Name), "The index name in the request body must match the name in the URL.");
            return BadRequest(ModelState);
        }

        if (existing != null && !string.Equals(index.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Name is init-only, so the body is re-read under the target's name
            var renamed = JsonSerializer.SerializeToNode(index, jsonSerializerOptions)!.AsObject();
            renamed["name"] = existing.Name;
            index = renamed.Deserialize<SearchIndex>(jsonSerializerOptions)!;
        }

        if (ValidateComplexFields(index) is { } complexError)
        {
            return BadRequest(complexError);
        }

        if (ScoringProfileValidator.FindInvalidProfile(index) is { } scoringError)
        {
            return BadRequest(scoringError);
        }

        if (VectorSearchValidator.FindInvalidVectorSearch(index) is { } vectorError)
        {
            return BadRequest(vectorError);
        }

        if (AnalyzerValidator.FindInvalidAnalyzer(index) is { } analyzerError)
        {
            return BadRequest(analyzerError);
        }

        if (NormalizerValidator.FindInvalidNormalizer(index) is { } normalizerError)
        {
            return BadRequest(normalizerError);
        }

        if (await FindInvalidSynonymMapReference(index) is { } synonymMapError)
        {
            return BadRequest(synonymMapError);
        }

        if (existing == null)
        {
            if (await aliasRepository.Get(index.Name) != null)
            {
                return Conflict($"Index name '{index.Name}' is already used by an alias.");
            }

            await searchIndexRepository.Create(index);
            return IndexJson(index, StatusCodes.Status201Created);
        }

        if (IndexSchemaChangeValidator.FindDisallowedChange(existing, index) is { } schemaError)
        {
            return BadRequest(schemaError);
        }

        await searchIndexRepository.Update(index);

        // Clear cached Lucene resources so schema changes take effect. Writer first: it
        // holds the directory's write.lock and was built with the OLD per-field analyzer,
        // so it must be released before the reader and directory it depends on.
        luceneIndexWriterFactory.ClearCachedWriter(index.Name);
        luceneIndexReaderFactory.ClearCachedReader(index.Name);
        luceneDirectoryFactory.ClearCachedDirectory(index.Name);

        return IndexJson(index);
    }

    /// <summary>
    /// Azure's index statistics (issue #91): the document count and the bytes the index occupies.
    /// </summary>
    /// <remarks>
    /// <c>storageSize</c> is the size of the index's Lucene files, which is what the emulator
    /// actually stores; Azure's figure comes from its own storage format, so the two are not
    /// comparable beyond showing how an index grows. Vectors are stored in the same Lucene files
    /// rather than in a separate vector index, so <c>vectorIndexSize</c> is always <c>0</c>.
    /// <para>
    /// Takes an index name only, not an alias, like deletion: statistics describe one physical
    /// index.
    /// </para>
    /// </remarks>
    [HttpGet]
    [Route("indexes({key})/search.stats")]
    [Route("indexes/{key}/stats")]
    public async Task<IActionResult> GetStatistics(string key)
    {
        // Strip quotes that may be captured from OData-style URLs
        key = key.Trim('\'');

        var index = await searchIndexRepository.Get(key);

        if (index == null)
        {
            return NotFound();
        }

        var statistics = new JsonObject
        {
            ["@odata.context"] = $"{Request.Scheme}://{Request.Host}{Request.PathBase}/$metadata#Microsoft.Azure.Search.V2024_07_01.IndexStatistics",
            ["documentCount"] = await indexSearcher.GetDocCount(index),
            ["storageSize"] = LuceneStorage.GetSize(luceneDirectoryFactory.GetDirectory(index.Name)),
            ["vectorIndexSize"] = 0L
        };

        return new ContentResult
        {
            Content = statistics.ToJsonString(),
            ContentType = "application/json",
            StatusCode = StatusCodes.Status200OK
        };
    }

    /// <remarks>
    /// Never resolves an alias, as in Azure. An index an alias still targets cannot be deleted
    /// until the alias is deleted or re-pointed, which Azure answers with <c>400</c>.
    /// </remarks>
    [HttpDelete]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        // Strip quotes that may be captured from OData-style URLs
        key = key.Trim('\'');

        var index = await searchIndexRepository.Get(key);

        if (index == null)
        {
            return NotFound();
        }

        var aliases = new List<string>();

        await foreach (var searchAlias in aliasRepository.GetAll())
        {
            if (searchAlias.Indexes.Any(i => string.Equals(i, index.Name, StringComparison.OrdinalIgnoreCase)))
            {
                aliases.Add(searchAlias.Name);
            }
        }

        if (aliases.Count > 0)
        {
            return BadRequest(
                $"Index '{index.Name}' is mapped to alias(es) {string.Join(", ", aliases.Select(a => $"'{a}'"))}. " +
                "Delete the alias(es) or map them to a different index before deleting the index.");
        }

        // Release Lucene resources BEFORE deleting from disk: the repository does a
        // recursive Directory.Delete of the segment files, and a cached writer holds
        // write.lock plus open handles on them. On Linux the unlink would silently
        // succeed and leave the writer appending to orphaned inodes; on Windows it
        // would throw. Order matters — writer, then reader, then directory.
        luceneIndexWriterFactory.ClearCachedWriter(index.Name);
        luceneIndexReaderFactory.ClearCachedReader(index.Name);
        luceneDirectoryFactory.ClearCachedDirectory(index.Name);

        await searchIndexRepository.Delete(index);

        return NoContent();
    }

    /// <summary>
    /// Writes a single index definition to the response with System.Text.Json rather than
    /// letting OData serialize it (issue #41).
    /// </summary>
    /// <remarks>
    /// <c>ODataOutputFormatter</c> serializes strictly from the EDM model, so the
    /// properties captured in <see cref="SearchIndex.AdditionalProperties"/> cannot survive it:
    /// while the extension-data bag was part of the model it emitted them as
    /// <c>"additionalProperties": [{}, {}]</c>, and once ignored there they vanished from the
    /// response altogether. Either way a client reading its index back lost the very properties
    /// the emulator had correctly stored, and the get-modify-put cycle still destroyed them.
    ///
    /// Serializing here keeps the fix contained to the single-index responses. The collection
    /// endpoint stays on OData, because the Azure Search SDK expects its <c>value</c> wrapper.
    /// The <c>@odata.context</c> annotation is dropped from these three responses as a result;
    /// the SDK does not read it, and returning the definition intact matters more.
    /// </remarks>
    private ContentResult IndexJson(SearchIndex index, int statusCode = StatusCodes.Status200OK)
    {
        if (statusCode == StatusCodes.Status201Created)
        {
            // Created() used to set this; keep emitting it in the OData key form it produced,
            // so a client following the header lands on the same URL as before.
            Response.Headers.Location =
                $"{Request.Scheme}://{Request.Host}{Request.PathBase}/indexes('{index.Name}')";
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(index, jsonSerializerOptions),
            ContentType = "application/json",
            StatusCode = statusCode
        };
    }

    /// <summary>
    /// Checks the synonym maps the index's fields name against the ones the service holds
    /// (issue #69).
    /// </summary>
    /// <remarks>
    /// Async, and so kept out of the static validator chain above: unlike every other check
    /// here, this one cannot be answered from the index definition alone, because a synonym map
    /// lives outside it.
    /// </remarks>
    private async Task<string?> FindInvalidSynonymMapReference(SearchIndex index)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var synonymMap in synonymMapRepository.GetAll())
        {
            names.Add(synonymMap.Name);
        }

        return SynonymMapValidator.FindInvalidFieldReference(index, names);
    }

    /// <summary>
    /// Checks the index's complex fields, returning an error message when one is malformed,
    /// or null when the schema is acceptable.
    /// </summary>
    /// <remarks>
    /// Azure Search rejects these at index creation rather than failing later during
    /// indexing, so they are caught here to keep the failure in the same place.
    /// </remarks>
    private static string? ValidateComplexFields(SearchIndex index)
    {
        try
        {
            foreach (var field in index.Fields)
            {
                ComplexTypeSupport.ValidateComplexField(field);
            }

            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }
}
