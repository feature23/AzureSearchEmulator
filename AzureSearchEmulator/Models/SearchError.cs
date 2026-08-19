using System.Text.Json.Serialization;

namespace AzureSearchEmulator.Models;

/// <summary>
/// Azure Search's error envelope: <c>{ "error": { "code": "...", "message": "..." } }</c>.
/// </summary>
/// <remarks>
/// The wrapper key is <c>error</c>, not the <c>odata.error</c> that the rest of the OData
/// surface might suggest (issue #40). This is not cosmetic: <c>Azure.Search.Documents</c>
/// reads <c>error.code</c> into <see cref="Azure.RequestFailedException.ErrorCode"/> and
/// <c>error.message</c> into its <c>Message</c>. Given an <c>odata.error</c> body — or the
/// bare strings the emulator used to return — the SDK finds neither, leaving ErrorCode null
/// and the message as the unhelpful "Service request failed."
/// </remarks>
public class SearchErrorResponse
{
    [JsonPropertyName("error")]
    public SearchError Error { get; set; } = new();
}

public class SearchError
{
    /// <summary>
    /// Azure leaves this empty for most request-validation failures, and the SDK is content
    /// with an empty string, so it is only populated where a specific code is known.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Nested errors that led to this one. The swagger declares this as a recursive array of
    /// the same type; it is omitted from the response when empty, which is how Azure reports
    /// a single failure.
    /// </summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<SearchError>? Details { get; set; }
}

/// <summary>
/// The error codes the emulator has a specific value for. Azure leaves <c>code</c> empty for
/// most query-time failures, so this covers only the cases where a real code is known.
/// </summary>
public static class SearchErrorCodes
{
    public const string ResourceNameAlreadyInUse = "ResourceNameAlreadyInUse";
}
