using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

public interface ISearchIndexRepository
{
    IAsyncEnumerable<SearchIndex> GetAll();

    Task<SearchIndex?> Get(string key);

    Task Create(SearchIndex index);

    Task<bool> Delete(SearchIndex index);
}
