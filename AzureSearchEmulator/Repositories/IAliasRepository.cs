using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

/// <summary>
/// Stores the service's index aliases.
/// </summary>
/// <remarks>
/// Separate from <see cref="ISearchIndexRepository"/> for the reason given on
/// <see cref="ISynonymMapRepository"/>: an alias is a service-level resource with its own routes
/// and lifetime. Whether its target exists is a controller concern; the repository only stores.
/// </remarks>
public interface IAliasRepository
{
    IAsyncEnumerable<SearchAlias> GetAll();

    Task<SearchAlias?> Get(string key);

    Task Create(SearchAlias searchAlias);

    Task Update(SearchAlias searchAlias);

    Task<bool> Delete(SearchAlias searchAlias);
}
