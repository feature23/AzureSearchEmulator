using System.Diagnostics;
using Azure.Core;
using Azure.Core.Pipeline;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// One HTTP round trip as the SDK made it, for the search page's request and response views
/// (issue #92).
/// </summary>
/// <param name="Response">Null when the request never got an answer, such as a refused connection.</param>
public record HttpExchange(
    string Method,
    string PathAndQuery,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    string? RequestBody,
    HttpExchangeResponse? Response,
    TimeSpan Duration);

public record HttpExchangeResponse(
    int Status,
    string ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string? Body);

/// <summary>
/// An SDK pipeline policy that keeps the most recent request and response passing through it.
/// </summary>
/// <remarks>
/// Recorded inside the SDK's pipeline rather than in an <see cref="HttpMessageHandler"/> beneath it,
/// so that the headers are the ones the SDK sent and the address is the one it addressed — not the
/// loopback binding <see cref="EmulatorClientFactory"/> quietly rewrites it to. It sits per retry,
/// after the policies that stamp the api-key, user agent and request id, and before the one that
/// buffers the response body, so by the time the next policy returns the body is in memory and can
/// be read without taking it away from the SDK's own deserialization.
/// </remarks>
public sealed class HttpExchangeRecorder : HttpPipelinePolicy
{
    public HttpExchange? LastExchange { get; private set; }

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
        ProcessAsync(message, pipeline, async: false).AsTask().GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) =>
        ProcessAsync(message, pipeline, async: true);

    private async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline, bool async)
    {
        var request = message.Request;
        var requestBody = await ReadRequestBodyAsync(request.Content, async, message.CancellationToken);
        var started = Stopwatch.GetTimestamp();

        try
        {
            if (async)
            {
                await ProcessNextAsync(message, pipeline);
            }
            else
            {
                ProcessNext(message, pipeline);
            }
        }
        finally
        {
            LastExchange = new HttpExchange(
                request.Method.Method,
                request.Uri.ToUri().PathAndQuery,
                ToList(request.Headers),
                requestBody,
                message.HasResponse ? ToResponse(message.Response) : null,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private static async ValueTask<string?> ReadRequestBodyAsync(RequestContent? content, bool async, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        using var stream = new MemoryStream();

        if (async)
        {
            await content.WriteToAsync(stream, cancellationToken);
        }
        else
        {
            content.WriteTo(stream, cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static HttpExchangeResponse ToResponse(Azure.Response response)
    {
        string? body;

        try
        {
            body = response.Content.ToString();
        }
        catch (InvalidOperationException)
        {
            // Content throws when the body was left as a live stream instead of being buffered.
            // Nothing the page sends opts out of buffering, so this is defensive.
            body = null;
        }

        return new HttpExchangeResponse(response.Status, response.ReasonPhrase, ToList(response.Headers), body);
    }

    private static List<KeyValuePair<string, string>> ToList(IEnumerable<HttpHeader> headers) =>
        headers.Select(i => KeyValuePair.Create(i.Name, i.Value)).ToList();
}
