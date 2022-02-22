namespace AzureSearchEmulator.Models;

public class SearchIndexExistsException : Exception
{
    public SearchIndexExistsException(string indexKey)
        : base($"Index with key {indexKey} already exists")
    {
    }
}