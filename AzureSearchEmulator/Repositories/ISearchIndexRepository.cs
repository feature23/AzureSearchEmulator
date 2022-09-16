using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

public interface ISearchIndexRepository
{
    IEnumerable<SearchIndex> GetAll();

    Task<SearchIndex?> Get(string key);

    Task Create(SearchIndex index);
    
    Task<bool> Delete(SearchIndex index);
}