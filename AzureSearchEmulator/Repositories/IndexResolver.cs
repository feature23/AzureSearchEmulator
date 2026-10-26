using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

/// <remarks>
/// An index is looked up first. The two namespaces cannot overlap — creating either refuses a
/// name the other holds — so the order only matters for a directory edited by hand. An index
/// cannot be deleted while an alias targets it, so an alias whose target is gone also comes only
/// from a hand edit; it resolves to null, which the routes report as a missing index.
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

        var searchAlias = await aliasRepository.Get(indexOrAliasName);

        return searchAlias?.Indexes.Count == 1 ? await searchIndexRepository.Get(searchAlias.Indexes[0]) : null;
    }
}
