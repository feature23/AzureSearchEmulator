namespace AzureSearchEmulator.Models;

public class SynonymMapExistsException(string synonymMapKey) : Exception($"Synonym map with key {synonymMapKey} already exists");
