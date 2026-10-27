using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace AzureSearchEmulator.Dashboard;

/// <summary>
/// Creates Azure SDK clients that talk to this emulator, for the dashboard's search and index pages
/// (issues #91 and #92).
/// </summary>
/// <remarks>
/// <para>
/// The page goes through the SDK and the HTTP API rather than calling the searcher directly so that
/// what it shows is what an application would get: the same parsing, the same error envelope, the
/// same response shape, and a raw request and response that can be copied into a real client.
/// </para>
/// <para>
/// The SDK refuses any endpoint that is not https, which the emulator's default plain-HTTP binding
/// cannot satisfy. So the SDK is handed an https URI on the right port and the transport rewrites
/// each request onto whichever binding <see cref="LoopbackEndpoint"/> picks. That transport also
/// skips certificate validation, which the SDK itself never allows: the connection is from this
/// process to its own listener, so there is no one to authenticate, and the certificate the
/// emulator serves is frequently one this process has no reason to trust — the integration tests'
/// throwaway certificate, or a host's dev certificate mounted into a container.
/// </para>
/// </remarks>
public sealed class EmulatorClientFactory : IDisposable
{
    // The emulator ignores keys, but the SDK will not build a client without one.
    private static readonly AzureKeyCredential Credential = new("dashboard");

    private readonly IServer _server;
    private readonly HttpClient _httpClient;

    // Resolved on first use rather than in the constructor: Kestrel only knows its addresses once it
    // has started, and PublicationOnly keeps a failure from being cached for the process lifetime.
    private readonly Lazy<Uri> _loopback;

    public EmulatorClientFactory(IServer server)
    {
        _server = server;
        _loopback = new Lazy<Uri>(ResolveLoopback, LazyThreadSafetyMode.PublicationOnly);
        _httpClient = new HttpClient(new LoopbackRewriteHandler(() => _loopback.Value)
        {
            InnerHandler = new SocketsHttpHandler
            {
                SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
            }
        });
    }

    /// <summary>
    /// The URI handed to the SDK. Only the path and query of the requests it builds survive the
    /// rewrite, so the host is nominal.
    /// </summary>
    private static Uri SdkEndpoint { get; } = new("https://localhost");

    public SearchIndexClient CreateIndexClient(HttpExchangeRecorder? recorder = null) =>
        new(SdkEndpoint, Credential, CreateOptions(recorder));

    /// <param name="indexName">The index (or alias) to query.</param>
    /// <param name="recorder">
    /// Captures the raw exchange for the page to show. Per client rather than shared, so that two
    /// users searching at once each see their own request.
    /// </param>
    public SearchClient CreateSearchClient(string indexName, HttpExchangeRecorder? recorder) =>
        new(SdkEndpoint, indexName, Credential, CreateOptions(recorder));

    private SearchClientOptions CreateOptions(HttpExchangeRecorder? recorder)
    {
        var options = new SearchClientOptions
        {
            Transport = new HttpClientTransport(_httpClient),
            // A failed query from the page should show its error immediately, not after the SDK's
            // backoff has retried a request that is going to fail the same way every time.
            Retry = { MaxRetries = 0 }
        };

        if (recorder is not null)
        {
            options.AddPolicy(recorder, Azure.Core.HttpPipelinePosition.PerRetry);
        }

        return options;
    }

    private Uri ResolveLoopback()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

        return LoopbackEndpoint.Resolve(addresses)
               ?? throw new InvalidOperationException(
                   "The emulator is not listening on any HTTP or HTTPS address the dashboard can reach.");
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed class LoopbackRewriteHandler(Func<Uri> loopback) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri)
            {
                request.RequestUri = new Uri(loopback(), uri.PathAndQuery);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
