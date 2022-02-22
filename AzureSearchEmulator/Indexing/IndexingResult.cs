namespace AzureSearchEmulator.Indexing;

public class IndexingResult
{
    public IndexingResult(string key, bool status, int statusCode)
    {
        Key = key;
        Status = status;
        StatusCode = statusCode;
    }

    public IndexingResult(string key, string? errorMessage, bool status, int statusCode)
    {
        Key = key;
        ErrorMessage = errorMessage;
        Status = status;
        StatusCode = statusCode;
    }

    public string Key { get; }

    public string? ErrorMessage { get; }

    public bool Status { get; }

    public int StatusCode { get; }
}