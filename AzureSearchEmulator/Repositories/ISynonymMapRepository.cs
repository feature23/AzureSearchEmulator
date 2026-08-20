using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

/// <summary>
/// Stores the service's synonym maps (issue #69).
/// </summary>
/// <remarks>
/// Separate from <see cref="ISearchIndexRepository"/> because a synonym map is a service-level
/// resource rather than part of an index: it is created, edited and deleted on its own routes,
/// and the indexes that name it are unaware of when that happens.
/// </remarks>
public interface ISynonymMapRepository
{
    IAsyncEnumerable<SynonymMap> GetAll();

    Task<SynonymMap?> Get(string key);

    Task Create(SynonymMap synonymMap);

    Task Update(SynonymMap synonymMap);

    Task<bool> Delete(SynonymMap synonymMap);
}
