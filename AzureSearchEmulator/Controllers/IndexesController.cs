using System.Text.Json;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace AzureSearchEmulator.Controllers;

public class IndexesController : ODataController
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly ISearchIndexRepository _searchIndexRepository;

    public IndexesController(JsonSerializerOptions jsonSerializerOptions, 
        ISearchIndexRepository searchIndexRepository)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
        _searchIndexRepository = searchIndexRepository;
    }

    [HttpGet]
    [EnableQuery]
    [Route("indexes")]
    public IAsyncEnumerable<SearchIndex> Get()
    {
        return _searchIndexRepository.GetAll();
    }

    [HttpGet]
    [Route("indexes({key})")]
    [Route("indexes/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var index = await _searchIndexRepository.Get(key);

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
        var index = JsonSerializer.Deserialize<SearchIndex>(indexJson, _jsonSerializerOptions);

        if (index == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            await _searchIndexRepository.Create(index);
        }
        catch (SearchIndexExistsException)
        {
            return Conflict();
        }

        return Created(index);
    }
}