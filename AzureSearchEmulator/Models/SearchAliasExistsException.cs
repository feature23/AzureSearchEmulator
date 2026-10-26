namespace AzureSearchEmulator.Models;

public class SearchAliasExistsException(string aliasKey) : Exception($"Alias with key {aliasKey} already exists");
