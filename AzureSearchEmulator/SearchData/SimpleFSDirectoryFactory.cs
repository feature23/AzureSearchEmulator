using System.Collections.Concurrent;
using Lucene.Net.Store;
using Microsoft.Extensions.Options;
using Directory = Lucene.Net.Store.Directory;

namespace AzureSearchEmulator.SearchData;

public class SimpleFSDirectoryFactory(IOptions<EmulatorOptions> options) : ILuceneDirectoryFactory
{
    private readonly EmulatorOptions _options = options.Value;

    private readonly ConcurrentDictionary<string, Directory> _directories = new ConcurrentDictionary<string, Directory>();

    public Directory GetDirectory(string indexName)
    {
        indexName = indexName.ToLowerInvariant();

        if (_directories.TryGetValue(indexName, out var directory))
        {
            return directory;
        }

        var path = Path.Join(Path.GetFullPath(_options.IndexesDirectory), indexName.ToLowerInvariant());

        directory = new SimpleFSDirectory(path);

        _directories.TryAdd(indexName, directory);

        return directory;
    }

    public void ClearCachedDirectory(string indexName)
    {
        indexName = indexName.ToLowerInvariant();

        if (_directories.TryRemove(indexName, out var directory))
        {
            directory.Dispose();
        }
    }
}
