using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

/// <remarks>
/// An index is looked up first. The two namespaces cannot overlap — creating either refuses a
/// name the other holds — so the order only matters for a directory edited by hand. An alias
/// whose target was deleted resolves to null, which the routes report as a missing index.
/// </remarks>
public class IndexResolver(ISearchIndexRepository searchIndexRepository, IAliasRepository aliasRepository)
    : IIndexResolver
{
    public async Task<SearchIndex?> Get(string indexOrAliasName)
    {
        var index = await searchIndexRepository.Get(indexOrAliasName);

        if (index != null)
        {
            return index;
        }

        var alias = await aliasRepository.Get(indexOrAliasName);

        return alias?.Indexes.Count == 1 ? await searchIndexRepository.Get(alias.Indexes[0]) : null;
    }
}
