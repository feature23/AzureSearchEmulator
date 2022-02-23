using System.Text.Json;
using AzureSearchEmulator.Models;
using Microsoft.Extensions.Options;
using static System.IO.File;

namespace AzureSearchEmulator.Repositories;

public class FileSearchIndexRepository : ISearchIndexRepository
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;
    private readonly EmulatorOptions _options;

    public FileSearchIndexRepository(JsonSerializerOptions jsonSerializerOptions, IOptions<EmulatorOptions> options)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
        _options = options.Value;
    }

    public async IAsyncEnumerable<SearchIndex> GetAll()
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            yield break;
        }

        var files = Directory.GetFiles(_options.IndexesDirectory, "*.index.json");

        foreach (var file in files)
        {
            yield return JsonSerializer.Deserialize<SearchIndex>(await ReadAllTextAsync(file), _jsonSerializerOptions)
                         ?? throw new InvalidOperationException($"Invalid search index definition file: {file}");
        }
    }

    public async Task<SearchIndex?> Get(string key)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return null;
        }

        string file = GetIndexFileName(key);

        if (!Exists(file))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SearchIndex>(await ReadAllTextAsync(file), _jsonSerializerOptions);
    }

    public async Task Create(SearchIndex index)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            Directory.CreateDirectory(_options.IndexesDirectory);
        }

        string file = GetIndexFileName(index.Name);

        if (Exists(file))
        {
            throw new SearchIndexExistsException(index.Name);
        }

        string json = JsonSerializer.Serialize(index, _jsonSerializerOptions);

        await WriteAllTextAsync(file, json);
    }

    public Task<bool> Delete(SearchIndex index)
    {
        if (!Directory.Exists(_options.IndexesDirectory))
        {
            return Task.FromResult(false);
        }

        string file = GetIndexFileName(index.Name);

        if (!Exists(file))
        {
            return Task.FromResult(false);
        }

        File.Delete(file);

        string folder = GetIndexFolderName(index.Name);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }

        return Task.FromResult(true);
    }

    private string GetIndexFileName(string key) => Path.Combine(_options.IndexesDirectory, $"{key.ToLowerInvariant()}.index.json");

    private string GetIndexFolderName(string key) => Path.Combine(_options.IndexesDirectory, key.ToLowerInvariant());
}