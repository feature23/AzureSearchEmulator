using Azure;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace AzureSearchEmulator.IntegrationTests;

/// <summary>
/// Factory for creating and managing an Azure Search Emulator container using Testcontainers.
/// The container runs the emulator built from the Dockerfile in the repository root.
/// </summary>
public class EmulatorFactory : IAsyncLifetime, IAsyncDisposable
{
    private readonly int _httpsPort = Random.Shared.Next(5000, 60000);

    private IContainer? _container;

    /// <summary>
    /// Gets the HTTPS endpoint URI for the running emulator container.
    /// </summary>
    private Uri Endpoint { get; set; } = null!;

    /// <summary>
    /// Starts the emulator container.
    /// Must be called before using the SearchIndexClient or SearchClient.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "..")
            .WithCleanUp(true)
            .Build();

        _container = new ContainerBuilder(image)
            .WithPortBinding(_httpsPort, _httpsPort)
            .WithEnvironment("ASPNETCORE_URLS", $"https://+:{_httpsPort}")
            .WithEnvironment("ASPNETCORE_HTTPS_PORT", _httpsPort.ToString())
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Password", "password")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/app/aspnetapp.pfx")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(_httpsPort)) // NOTE: we cannot use HTTP wait strategy due to self-signed cert
            .Build();

        await image.CreateAsync();
        await _container.StartAsync();

        // Get the mapped HTTPS port
        var mappedPort = _container.GetMappedPublicPort(_httpsPort);
        Endpoint = new Uri($"https://localhost:{mappedPort}");
    }

    /// <summary>
    /// Gets a key credential for use with the Azure Search SDK (any key works for the emulator).
    /// </summary>
    private static AzureKeyCredential Credential { get; } = new("test-key");

    /// <summary>
    /// Creates a SearchIndexClient configured for testing against the emulator.
    /// </summary>
    public SearchIndexClient CreateSearchIndexClient()
    {
        var options = new SearchClientOptions
        {
            Transport = new HttpClientTransport(CreateHandler()),
            Retry = { MaxRetries = 3 }
        };

        return new SearchIndexClient(Endpoint, Credential, options);
    }

    /// <summary>
    /// Creates a SearchClient configured for testing against the emulator.
    /// </summary>
    public SearchClient CreateSearchClient(string indexName)
    {
        var options = new SearchClientOptions
        {
            Transport = new HttpClientTransport(CreateHandler()),
            Retry = { MaxRetries = 3 }
        };

        return new SearchClient(Endpoint, indexName, Credential, options);
    }

    /// <summary>
    /// Creates a raw <see cref="HttpClient"/> pointed at the emulator, for tests that need
    /// to send payloads the strongly-typed SDK will not construct — such as a batch item
    /// missing its key field.
    /// </summary>
    /// <remarks>
    /// This client has no retry policy, unlike the SDK clients. A test whose first contact
    /// with the container is through this client should call
    /// <see cref="WaitUntilServingAsync"/> first.
    /// </remarks>
    public HttpClient CreateHttpClient() => new(CreateHandler()) { BaseAddress = Endpoint };

    /// <summary>
    /// Blocks until the emulator is actually serving requests.
    /// </summary>
    /// <remarks>
    /// The container's wait strategy only checks that the TCP port accepts connections, which
    /// happens before Kestrel has finished negotiating TLS. A raw <see cref="HttpClient"/>
    /// arriving in that window fails the handshake outright with "Received an unexpected EOF
    /// or 0 bytes from the transport stream" — a client-side transport failure that reads
    /// like a server fault but is not one.
    ///
    /// Most tests never notice, because they reach the emulator through an SDK client whose
    /// three retries cover the gap. This makes that same tolerance available to the tests
    /// that start with the raw client, by issuing one retried SDK call and discarding the
    /// result.
    /// </remarks>
    public async Task WaitUntilServingAsync()
    {
        // GetServiceStatisticsAsync is the cheapest call that needs no index to exist.
        await CreateSearchIndexClient().GetServiceStatisticsAsync();
    }

    /// <summary>
    /// Builds the shared HTTP handler for talking to the emulator container.
    /// </summary>
    /// <remarks>
    /// MaxConnectionsPerServer is raised well above the default because the concurrency
    /// tests deliberately fire hundreds of simultaneous requests at a single container.
    /// At the default limit the surplus requests queue on the connection pool, time out
    /// mid-TLS-handshake, and surface as "IOException: Received an unexpected EOF or
    /// 0 bytes from the transport stream" — a client-side transport failure that looks
    /// like a server fault but is not one.
    /// </remarks>
    private static HttpClientHandler CreateHandler() => new()
    {
        // Test environment only: the emulator serves a self-signed certificate.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        MaxConnectionsPerServer = 256
    };

    /// <summary>
    /// Stops and disposes the emulator container.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_container != null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
