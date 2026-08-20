using System.Text.Json;
using AzureSearchEmulator.Models;
using Microsoft.Extensions.Options;
using static System.IO.File;

namespace AzureSearchEmulator.Repositories;

/// <summary>
/// Keeps each synonym map in its own JSON file, next to the index definitions.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="FileSearchIndexRepository"/>, down to the
/// lower-cased file name and the guard on path characters — a synonym map name arrives from the
/// same untrusted route an index name does, and <c>..</c> in it would otherwise escape the
/// directory.
///
/// Unlike an index, a map owns no Lucene directory, so <see cref="Delete"/> has only the one
/// file to remove.
/// </remarks>
public class FileSynonymMapRepository(JsonSerializerOptions jsonSerializerOptions, IOptions<EmulatorOptions> options)
    : ISynonymMapRepository
{
    private const string FileSuffix = ".synonymmap.json";

    private readonly EmulatorOptions _options = options.Value;

    public async IAsyncEnumerable<SynonymMap> GetAll()
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            yield break;
        }

        var files = Directory.GetFiles(_options.IndexesDirectory, $"*{FileSuffix}");

        foreach (var file in files)
        {
            yield return JsonSerializer.Deserialize<SynonymMap>(await ReadAllTextAsync(file), jsonSerializerOptions)
                         ?? throw new InvalidOperationException($"Invalid synonym map definition file: {file}");
        }
    }

    public async Task<SynonymMap?> Get(string key)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return null;
        }

        string file = GetSynonymMapFileName(key);

        if (!Exists(file))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SynonymMap>(await ReadAllTextAsync(file), jsonSerializerOptions);
    }

    public Task Create(SynonymMap synonymMap)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            Directory.CreateDirectory(_options.IndexesDirectory);
        }

        string file = GetSynonymMapFileName(synonymMap.Name);

        if (Exists(file))
        {
            throw new SynonymMapExistsException(synonymMap.Name);
        }

        return WriteAllTextAsync(file, JsonSerializer.Serialize(synonymMap, jsonSerializerOptions));
    }

    public Task Update(SynonymMap synonymMap)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            Directory.CreateDirectory(_options.IndexesDirectory);
        }

        string file = GetSynonymMapFileName(synonymMap.Name);

        return WriteAllTextAsync(file, JsonSerializer.Serialize(synonymMap, jsonSerializerOptions));
    }

    public Task<bool> Delete(SynonymMap synonymMap)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return Task.FromResult(false);
        }

        string file = GetSynonymMapFileName(synonymMap.Name);

        if (!Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);

        return Task.FromResult(true);
    }

    private string GetSynonymMapFileName(string key)
    {
        if (key.Contains('.') || key.Contains('/') || key.Contains('\\'))
        {
            throw new ArgumentException("Synonym map file name cannot contain any of the following characters: . \\ /");
        }

        return Path.Combine(_options.IndexesDirectory, $"{key.ToLowerInvariant()}{FileSuffix}");
    }
}
