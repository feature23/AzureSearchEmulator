namespace AzureSearchEmulator.Models;

public class SearchIndexExistsException(string indexKey) : Exception($"Index with key {indexKey} already exists");
