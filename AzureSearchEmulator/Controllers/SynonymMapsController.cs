using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;
using SynonymMap = AzureSearchEmulator.Models.SynonymMap;

namespace AzureSearchEmulator.Controllers;

/// <summary>
/// The service-level <c>/synonymmaps</c> routes (issue #69).
/// </summary>
/// <remarks>
/// A plain <see cref="ControllerBase"/> rather than an <c>ODataController</c>, and every
/// response is written with System.Text.Json. <see cref="IndexesController.IndexJson"/> explains
/// why that matters for a type with a <c>[JsonExtensionData]</c> bag: OData serializes strictly
/// from the EDM model, so a map's <c>encryptionKey</c> — which the emulator stores but does not
/// model — would come back as an empty object or vanish. Unlike the index routes there is no
/// paired collection endpoint left on OData, so the <c>value</c> wrapper the Azure Search SDK
/// expects from a listing is written by hand in <see cref="Get"/>.
/// </remarks>
[ApiController]
public class SynonymMapsController(
    JsonSerializerOptions jsonSerializerOptions,
    ISynonymMapRepository synonymMapRepository)
    : ControllerBase
{
    [HttpGet]
    [Route("synonymmaps")]
    public async Task<IActionResult> Get()
    {
        var synonymMaps = new List<SynonymMap>();

        await foreach (var synonymMap in synonymMapRepository.GetAll())
        {
            synonymMaps.Add(synonymMap);
        }

        // The SDK reads a listing out of "value"; returning the bare array fails to deserialize.
        return Json(new { value = synonymMaps });
    }

    [HttpGet]
    [Route("synonymmaps({key})")]
    [Route("synonymmaps/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        var synonymMap = await synonymMapRepository.Get(key.Trim('\''));

        if (synonymMap == null)
        {
            return NotFound();
        }

        return Json(synonymMap);
    }

    [HttpPost]
    [Route("synonymmaps")]
    public async Task<IActionResult> Post([FromBody] SynonymMap? synonymMap)
    {
        if (synonymMap == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (SynonymMapValidator.FindInvalidSynonymMap(synonymMap) is { } error)
        {
            return BadRequest(error);
        }

        try
        {
            await synonymMapRepository.Create(synonymMap);
        }
        catch (SynonymMapExistsException ex)
        {
            return Conflict(ex.Message);
        }

        return Json(synonymMap, StatusCodes.Status201Created);
    }

    /// <remarks>
    /// Create-or-update, which is what the SDK's <c>CreateOrUpdateSynonymMap</c> calls. Nothing
    /// about a map is immutable — the rules are read afresh on every search — so unlike an index
    /// there is no schema-change check to make here, and replacing the definition wholesale is
    /// the whole of the update.
    /// </remarks>
    [HttpPut]
    [Route("synonymmaps({key})")]
    [Route("synonymmaps/{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] SynonymMap? synonymMap)
    {
        key = key.Trim('\'');

        if (synonymMap == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!string.Equals(synonymMap.Name, key, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(
                nameof(synonymMap.Name),
                "The synonym map name in the request body must match the name in the URL.");

            return BadRequest(ModelState);
        }

        if (SynonymMapValidator.FindInvalidSynonymMap(synonymMap) is { } error)
        {
            return BadRequest(error);
        }

        var existing = await synonymMapRepository.Get(key);

        if (existing == null)
        {
            await synonymMapRepository.Create(synonymMap);

            return Json(synonymMap, StatusCodes.Status201Created);
        }

        await synonymMapRepository.Update(synonymMap);

        return Json(synonymMap);
    }

    [HttpDelete]
    [Route("synonymmaps({key})")]
    [Route("synonymmaps/{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        var synonymMap = await synonymMapRepository.Get(key.Trim('\''));

        if (synonymMap == null)
        {
            return NotFound();
        }

        await synonymMapRepository.Delete(synonymMap);

        return NoContent();
    }

    /// <summary>
    /// Writes a response body with System.Text.Json, for the reason given on the class.
    /// </summary>
    private ContentResult Json(object value, int statusCode = StatusCodes.Status200OK)
    {
        if (statusCode == StatusCodes.Status201Created)
        {
            Response.Headers.Location =
                $"{Request.Scheme}://{Request.Host}{Request.PathBase}/synonymmaps('{((SynonymMap)value).Name}')";
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(value, jsonSerializerOptions),
            ContentType = "application/json",
            StatusCode = statusCode
        };
    }
}
