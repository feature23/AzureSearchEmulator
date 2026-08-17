using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using AzureSearchEmulator.SearchData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class IndexesController(
    JsonSerializerOptions jsonSerializerOptions,
    ISearchIndexRepository searchIndexRepository,
    ILuceneDirectoryFactory luceneDirectoryFactory,
    ILuceneIndexReaderFactory luceneIndexReaderFactory,
    ILuceneIndexWriterFactory luceneIndexWriterFactory)
    : ODataController
{
    /// <remarks>
    /// This one still serializes through OData, so the properties held in
    /// <see cref="SearchIndex.AdditionalProperties"/> do not appear in the listing — see
    /// <see cref="IndexJson"/> for why the single-index responses had to leave OData to keep
    /// them. Moving this endpoint too would mean hand-writing the <c>value</c> wrapper the
    /// Azure Search SDK expects and dropping <c>[EnableQuery]</c>, so it is left as-is: a
    /// listing is a survey rather than the definition a client edits and writes back, and the
    /// round-trip that issue #41 is about goes through the single-index routes.
    /// </remarks>
    [HttpGet]
    [EnableQuery]
    [Route("indexes")]
    public IAsyncEnumerable<SearchIndex> Get()
    {
        return searchIndexRepository.GetAll();
    }

    [HttpGet]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var index = await searchIndexRepository.Get(key);

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

        try
        {
            await searchIndexRepository.Create(index);
        }
        catch (SearchIndexExistsException)
        {
            return Conflict();
        }

        return IndexJson(index, StatusCodes.Status201Created);
    }

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

        if (!string.Equals(index.Name, key, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(index.Name), "The index name in the request body must match the name in the URL.");
            return BadRequest(ModelState);
        }

        if (ValidateComplexFields(index) is { } complexError)
        {
            return BadRequest(complexError);
        }

        var existing = await searchIndexRepository.Get(key);

        if (existing == null)
        {
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
