namespace AzureSearchEmulator.Indexing;

public class IndexingResult(string key, string? errorMessage, bool status, int statusCode)
{
    public IndexingResult(string key, bool status, int statusCode)
        : this(key, null, status, statusCode)
    {
    }

    public string Key { get; } = key;

    public string? ErrorMessage { get; } = errorMessage;

    public bool Status { get; } = status;

    public int StatusCode { get; } = statusCode;
}
