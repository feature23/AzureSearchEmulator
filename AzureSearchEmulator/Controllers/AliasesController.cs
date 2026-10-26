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
///
/// Unlike synonym maps, aliases carry an ETag that every write renews, and <c>PUT</c> and
/// <c>DELETE</c> honour <c>If-Match</c> and <c>If-None-Match</c> with <c>412</c>. Re-pointing an
/// alias is how traffic is cut over between indexes, so it is the write most worth guarding
/// against a concurrent one.
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

        await foreach (var searchAlias in aliasRepository.GetAll())
        {
            aliases.Add(searchAlias);
        }

        return Json(new { value = aliases });
    }

    [HttpGet]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Get(string key)
    {
        key = key.Trim('\'');

        if (!AliasValidator.IsValidLookupName(key))
        {
            return NotFound();
        }

        var searchAlias = await aliasRepository.Get(key);

        if (searchAlias == null)
        {
            return NotFound($"Alias '{key}' was not found.");
        }

        return Json(searchAlias);
    }

    [HttpPost]
    [Route("aliases")]
    public async Task<IActionResult> Post([FromBody] SearchAlias? searchAlias)
    {
        if (searchAlias == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (await FindInvalidAlias(searchAlias) is { } error)
        {
            return BadRequest(error);
        }

        searchAlias.ETag = NewETag();

        try
        {
            await aliasRepository.Create(searchAlias);
        }
        catch (SearchAliasExistsException ex)
        {
            return Conflict(ex.Message);
        }

        return Json(searchAlias, StatusCodes.Status201Created);
    }

    /// <remarks>
    /// Create-or-update. Re-pointing an alias is its whole purpose, so an update may change the
    /// target freely.
    /// </remarks>
    [HttpPut]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Put(string key, [FromBody] SearchAlias? searchAlias)
    {
        key = key.Trim('\'');

        if (searchAlias == null || !ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!string.Equals(searchAlias.Name, key, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(searchAlias.Name), "The alias name in the request body must match the name in the URL.");

            return BadRequest(ModelState);
        }

        if (await FindInvalidAlias(searchAlias) is { } error)
        {
            return BadRequest(error);
        }

        var existing = await aliasRepository.Get(key);

        if (!PreconditionsHold(existing))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        searchAlias.ETag = NewETag();

        if (existing == null)
        {
            await aliasRepository.Create(searchAlias);

            return Json(searchAlias, StatusCodes.Status201Created);
        }

        await aliasRepository.Update(searchAlias);

        return Json(searchAlias);
    }

    [HttpDelete]
    [Route("aliases({key})")]
    [Route("aliases/{key}")]
    public async Task<IActionResult> Delete(string key)
    {
        key = key.Trim('\'');

        if (!AliasValidator.IsValidLookupName(key))
        {
            return NotFound();
        }

        var searchAlias = await aliasRepository.Get(key);

        if (searchAlias == null)
        {
            return NotFound($"Alias '{key}' was not found.");
        }

        if (!PreconditionsHold(searchAlias))
        {
            return StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        await aliasRepository.Delete(searchAlias);

        return NoContent();
    }

    private async Task<string?> FindInvalidAlias(SearchAlias searchAlias)
    {
        if (AliasValidator.FindInvalidAlias(searchAlias) is { } error)
        {
            return error;
        }

        if (await searchIndexRepository.Get(searchAlias.Name) != null)
        {
            return $"Alias name '{searchAlias.Name}' is already used by an index.";
        }

        if (await searchIndexRepository.Get(searchAlias.Indexes[0]) == null)
        {
            return $"Alias '{searchAlias.Name}' targets index '{searchAlias.Indexes[0]}', which does not exist.";
        }

        return null;
    }

    /// <summary>
    /// Checks <c>If-Match</c> and <c>If-None-Match</c> against the stored alias, or null when
    /// there is none. <c>*</c> matches any alias that exists.
    /// </summary>
    private bool PreconditionsHold(SearchAlias? existing)
    {
        var ifMatch = ParseETags(Request.Headers.IfMatch);
        var ifNoneMatch = ParseETags(Request.Headers.IfNoneMatch);

        if (ifMatch.Count > 0 && !Matches(ifMatch, existing))
        {
            return false;
        }

        return ifNoneMatch.Count == 0 || !Matches(ifNoneMatch, existing);
    }

    private static bool Matches(List<string> etags, SearchAlias? existing) =>
        existing != null && (etags.Contains("*") || etags.Contains(existing.ETag ?? ""));

    private static List<string> ParseETags(IEnumerable<string?> values) =>
        values
            .SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

    /// <remarks>
    /// Quoted, as Azure's are, so the value in <c>@odata.etag</c> can be sent back in
    /// <c>If-Match</c> unchanged.
    /// </remarks>
    private static string NewETag() => $"\"0x{DateTime.UtcNow.Ticks:X}\"";

    private ContentResult Json(object value, int statusCode = StatusCodes.Status200OK)
    {
        if (value is SearchAlias { ETag: { } etag })
        {
            Response.Headers.ETag = etag;
        }

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
