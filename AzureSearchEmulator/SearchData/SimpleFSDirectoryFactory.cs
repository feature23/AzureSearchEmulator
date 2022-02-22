using System.Collections.Concurrent;
using Lucene.Net.Store;
using Microsoft.Extensions.Options;
using Directory = Lucene.Net.Store.Directory;

namespace AzureSearchEmulator.SearchData;

public class SimpleFSDirectoryFactory : ILuceneDirectoryFactory
{
    private readonly EmulatorOptions _options;

    private readonly IDictionary<string, Directory> _directories = new ConcurrentDictionary<string, Directory>();

    public SimpleFSDirectoryFactory(IOptions<EmulatorOptions> options)
    {
        _options = options.Value;
    }

    public Directory GetDirectory(string indexName)
    {
        indexName = indexName.ToLowerInvariant();

        if (_directories.TryGetValue(indexName, out var directory))
        {
            return directory;
        }

        var path = Path.Join(Path.GetFullPath(_options.IndexesDirectory), indexName.ToLowerInvariant());

        directory = new SimpleFSDirectory(path);

        _directories.Add(indexName, directory);

        return directory;
    }
}