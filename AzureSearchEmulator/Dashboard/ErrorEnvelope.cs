using System.Text.Json;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Reads the emulator's error envelope out of a recorded exchange, for the dashboard pages to show
/// when an SDK call fails (issues #91 and #92).
/// </summary>
public static class ErrorEnvelope
{
    /// <summary>
    /// The message from the emulator's error envelope. The SDK's own exception message appends the
    /// status, headers and raw content, which is more than a reader needs to see what went wrong.
    /// </summary>
    public static string? Message(HttpExchange? exchange)
    {
        if (exchange?.Response?.Body is not { Length: > 0 } body)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
