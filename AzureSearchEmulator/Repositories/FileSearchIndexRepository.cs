using System.Text.Json;
using AzureSearchEmulator.Models;

using static System.IO.File;

namespace AzureSearchEmulator.Repositories;

public class FileSearchIndexRepository : ISearchIndexRepository
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public FileSearchIndexRepository(JsonSerializerOptions jsonSerializerOptions)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    public async IAsyncEnumerable<SearchIndex> GetAll()
    {
        if (!Directory.Exists(Constants.IndexFolderName))
        {
            yield break;
        }

        var files = Directory.GetFiles(Constants.IndexFolderName, "*.index.json");

        foreach (var file in files)
        {
            yield return JsonSerializer.Deserialize<SearchIndex>(await ReadAllTextAsync(file), _jsonSerializerOptions)
                         ?? throw new InvalidOperationException($"Invalid search index definition file: {file}");
        }
    }

    public async Task<SearchIndex?> Get(string key)
    {
        if (!Directory.Exists(Constants.IndexFolderName))
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
        if (!Directory.Exists(Constants.IndexFolderName))
        {
            Directory.CreateDirectory(Constants.IndexFolderName);
        }

        string file = GetIndexFileName(index.Name.ToLowerInvariant());

        if (Exists(file))
        {
            throw new SearchIndexExistsException(index.Name);
        }

        string json = JsonSerializer.Serialize(index, _jsonSerializerOptions);

        await WriteAllTextAsync(file, json);
    }

    private static string GetIndexFileName(string key) => Path.Combine(Constants.IndexFolderName, $"{key}.index.json");
}