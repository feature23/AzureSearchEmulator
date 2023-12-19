using System.Text.Json;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class IndexesController(
    JsonSerializerOptions jsonSerializerOptions,
    ISearchIndexRepository searchIndexRepository)
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

    [HttpDelete]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        var index = await searchIndexRepository.Get(key);

        if (index == null)
        {
            return NotFound();
        }

        await searchIndexRepository.Delete(index);

        return NoContent();
    }
}
