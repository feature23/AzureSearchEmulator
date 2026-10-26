using System.Text.Json;
using AzureSearchEmulator.Models;
using Microsoft.Extensions.Options;
using static System.IO.File;

namespace AzureSearchEmulator.Repositories;

/// <summary>
/// Keeps each alias in its own JSON file, next to the index definitions.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="FileSearchIndexRepository"/>, down to the
/// lower-cased file name and the guard on path characters — an alias name arrives from the
/// same untrusted route an index name does, and <c>..</c> in it would otherwise escape the
/// directory.
///
/// Unlike an index, an alias owns no Lucene directory, so <see cref="Delete"/> has only the one
/// file to remove.
/// </remarks>
public class FileAliasRepository(JsonSerializerOptions jsonSerializerOptions, IOptions<EmulatorOptions> options)
    : IAliasRepository
{
    private const string FileSuffix = ".alias.json";

    private readonly EmulatorOptions _options = options.Value;

    public async IAsyncEnumerable<SearchAlias> GetAll()
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            yield break;
        }

        var files = Directory.GetFiles(_options.IndexesDirectory, $"*{FileSuffix}");

        foreach (var file in files)
        {
            yield return JsonSerializer.Deserialize<SearchAlias>(await ReadAllTextAsync(file), jsonSerializerOptions)
                         ?? throw new InvalidOperationException($"Invalid alias definition file: {file}");
        }
    }

    public async Task<SearchAlias?> Get(string key)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return null;
        }

        string file = GetAliasFileName(key);

        if (!Exists(file))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SearchAlias>(await ReadAllTextAsync(file), jsonSerializerOptions);
    }

    public Task Create(SearchAlias alias)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            Directory.CreateDirectory(_options.IndexesDirectory);
        }

        string file = GetAliasFileName(alias.Name);

        if (Exists(file))
        {
            throw new SearchAliasExistsException(alias.Name);
        }

        return WriteAllTextAsync(file, JsonSerializer.Serialize(alias, jsonSerializerOptions));
    }

    public Task Update(SearchAlias alias)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            Directory.CreateDirectory(_options.IndexesDirectory);
        }

        string file = GetAliasFileName(alias.Name);

        return WriteAllTextAsync(file, JsonSerializer.Serialize(alias, jsonSerializerOptions));
    }

    public Task<bool> Delete(SearchAlias alias)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return Task.FromResult(false);
        }

        string file = GetAliasFileName(alias.Name);

        if (!Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);

        return Task.FromResult(true);
    }

    private string GetAliasFileName(string key)
    {
        if (key.Contains('.') || key.Contains('/') || key.Contains('\\'))
        {
            throw new ArgumentException("Alias file name cannot contain any of the following characters: . \\ /");
        }

        return Path.Combine(_options.IndexesDirectory, $"{key.ToLowerInvariant()}{FileSuffix}");
    }
}
