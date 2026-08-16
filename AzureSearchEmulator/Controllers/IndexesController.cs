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

        return Ok(index);
    }

    [HttpPost]
    [Route("indexes")]
    public async Task<IActionResult> Post() //([FromBody] SearchIndex? index)
    {
        // HACK.PI: For some reason, having this as a parameter with [FromBody] fails to deserialize properly.
        using var sr = new StreamReader(Request.Body);
        var indexJson = await sr.ReadToEndAsync();
        var index = JsonSerializer.Deserialize<SearchIndex>(indexJson, jsonSerializerOptions);

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

        return Created(index);
    }

    [HttpPut]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Put(string key)
    {
        // Strip quotes that may be captured from OData-style URLs
        key = key.Trim('\'');

        // HACK.JS: For some reason, having this as a parameter with [FromBody] fails to deserialize properly.
        using var sr = new StreamReader(Request.Body);
        var indexJson = await sr.ReadToEndAsync();
        var index = JsonSerializer.Deserialize<SearchIndex>(indexJson, jsonSerializerOptions);

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
            return Created(index);
        }

        await searchIndexRepository.Update(index);

        // Clear cached Lucene resources so schema changes take effect. Writer first: it
        // holds the directory's write.lock and was built with the OLD per-field analyzer,
        // so it must be released before the reader and directory it depends on.
        luceneIndexWriterFactory.ClearCachedWriter(index.Name);
        luceneIndexReaderFactory.ClearCachedReader(index.Name);
        luceneDirectoryFactory.ClearCachedDirectory(index.Name);

        return Ok(index);
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
