using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Pulls one document out of a recorded response body, for the index page's raw view of the
/// document it is showing (issue #91).
/// </summary>
/// <remarks>
/// Taken from the body the emulator sent rather than re-serialized from the SDK's
/// <c>SearchDocument</c>, which would turn dates into strings of its own choosing and has no
/// converter for geography values.
/// </remarks>
public static class DocumentJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The first document in a search response, or the document a lookup returned, without the
    /// <c>@search.*</c> annotations a search adds to it. Null when the body holds no document.
    /// </summary>
    public static string? Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject response)
        {
            return null;
        }

        var document = response["value"] is JsonArray values
            ? values.FirstOrDefault() as JsonObject
            : response;

        if (document is null)
        {
            return null;
        }

        foreach (var name in document.Select(i => i.Key).Where(i => i.StartsWith("@search.", StringComparison.Ordinal)).ToList())
        {
            document.Remove(name);
        }

        return document.ToJsonString(WriteOptions);
    }
}
