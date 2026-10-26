using System.Text.Json;
using AzureSearchEmulator.Indexing;
using AzureSearchEmulator.Models;
using AzureSearchEmulator.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace AzureSearchEmulator.Controllers;

/// <summary>
/// The service-level <c>/aliases</c> routes.
/// </summary>
/// <remarks>
/// Shaped like <see cref="SynonymMapsController"/>, and for the same reasons: a plain
/// <see cref="ControllerBase"/> writing System.Text.Json, so properties the emulator does not
/// model survive a round trip, with the <c>value</c> wrapper of a listing written by hand.
/// </remarks>
[ApiController]
public class AliasesController(
    JsonSerializerOptions jsonSerializerOptions,
    IAliasRepository aliasRepository,
    ISearchIndexRepository searchIndexRepository)
    : ControllerBase
{
    [HttpGet]
    [Route("aliases")]
    public async Task<IActionResult> Get()
    {
        var aliases = new List<SearchAlias>();

        await foreach (var alias in aliasRepository.GetAll())
        {
            aliases.Add(alias);
        }

        return Json(new { value = aliases });
    }

    [HttpGet]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        key = key.Trim('\'');

        if (!AliasValidator.IsValidName(key))
        {
            return NotFound();
        }

        var alias = await aliasRepository.Get(key);

        if (alias == null)
        {
            return NotFound($"Alias '{key}' was not found.");
        }

        return Json(alias);
    }

    [HttpPost]
    [Route("aliases")]
    public async Task<IActionResult> Post([FromBody] SearchAlias? alias)
    {
        if (alias == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (await FindInvalidAlias(alias) is { } error)
        {
            return BadRequest(error);
        }

        try
        {
            await aliasRepository.Create(alias);
        }
        catch (SearchAliasExistsException ex)
        {
            return Conflict(ex.Message);
        }

        return Json(alias, StatusCodes.Status201Created);
    }

    /// <remarks>
    /// Create-or-update. Re-pointing an alias is its whole purpose, so an update may change the
    /// target freely.
    /// </remarks>
    [HttpPut]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] SearchAlias? alias)
    {
        key = key.Trim('\'');

        if (alias == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!string.Equals(alias.Name, key, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(alias.Name), "The alias name in the request body must match the name in the URL.");

            return BadRequest(ModelState);
        }

        if (await FindInvalidAlias(alias) is { } error)
        {
            return BadRequest(error);
        }

        if (await aliasRepository.Get(key) == null)
        {
            await aliasRepository.Create(alias);

            return Json(alias, StatusCodes.Status201Created);
        }

        await aliasRepository.Update(alias);

        return Json(alias);
    }

    [HttpDelete]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        key = key.Trim('\'');

        if (!AliasValidator.IsValidName(key))
        {
            return NotFound();
        }

        var alias = await aliasRepository.Get(key);

        if (alias == null)
        {
            return NotFound($"Alias '{key}' was not found.");
        }

        await aliasRepository.Delete(alias);

        return NoContent();
    }

    private async Task<string?> FindInvalidAlias(SearchAlias alias)
    {
        if (AliasValidator.FindInvalidAlias(alias) is { } error)
        {
            return error;
        }

        if (await searchIndexRepository.Get(alias.Name) != null)
        {
            return $"Alias name '{alias.Name}' is already used by an index.";
        }

        if (await searchIndexRepository.Get(alias.Indexes[0]) == null)
        {
            return $"Alias '{alias.Name}' targets index '{alias.Indexes[0]}', which does not exist.";
        }

        return null;
    }

    private ContentResult Json(object value, int statusCode = StatusCodes.Status200OK)
    {
        if (statusCode == StatusCodes.Status201Created)
        {
            Response.Headers.Location =
                $"{Request.Scheme}://{Request.Host}{Request.PathBase}/aliases('{((SearchAlias)value).Name}')";
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(value, jsonSerializerOptions),
            ContentType = "application/json",
            StatusCode = statusCode
        };
    }
}
