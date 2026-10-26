using AzureSearchEmulator.Models;

namespace AzureSearchEmulator.Repositories;

/// <summary>
/// Turns the name a document route was addressed with into an index, following an alias if the
/// name is one.
/// </summary>
public interface IIndexResolver
{
    /// <summary>
    /// Returns the index the name refers to, or null when it names neither an index nor an alias
    /// whose target still exists.
    /// </summary>
    Task<SearchIndex?> Get(string indexOrAliasName);
}
