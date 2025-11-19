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
public class EmulatorFactory : IAsyncLifetime
{
    private const int HttpsPort = 5081;

    private IContainer? _container;

    /// <summary>
    /// Gets the HTTPS endpoint URI for the running emulator container.
    /// </summary>
    private Uri Endpoint { get; set; } = null!;

    /// <summary>
    /// Starts the emulator container.
    /// Must be called before using the SearchIndexClient or SearchClient.
    /// </summary>
    public async Task InitializeAsync()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetProjectDirectory(), "..")
            .WithCleanUp(true)
            .Build();

        _container = new ContainerBuilder()
            .WithImage(image)
            .WithPortBinding(HttpsPort, HttpsPort)
            .WithEnvironment("ASPNETCORE_URLS", $"https://+:{HttpsPort}")
            .WithEnvironment("ASPNETCORE_HTTPS_PORT", HttpsPort.ToString())
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Password", "password")
            .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/app/aspnetapp.pfx")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(HttpsPort))
            .Build();

        await image.CreateAsync();
        await _container.StartAsync();

        // Get the mapped HTTPS port
        var mappedPort = _container.GetMappedPublicPort(HttpsPort);
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
        var handler = new HttpClientHandler();
        // Allow untrusted certificates for testing
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var options = new SearchClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)),
            Retry = { MaxRetries = 1 } // Reduce retries to speed up tests
        };

        return new SearchIndexClient(Endpoint, Credential, options);
    }

    /// <summary>
    /// Creates a SearchClient configured for testing against the emulator.
    /// </summary>
    public SearchClient CreateSearchClient(string indexName)
    {
        var handler = new HttpClientHandler();
        // Allow untrusted certificates for testing
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var options = new SearchClientOptions
        {
            Transport = new HttpClientTransport(new HttpClient(handler)),
            Retry = { MaxRetries = 1 } // Reduce retries to speed up tests
        };

        return new SearchClient(Endpoint, indexName, Credential, options);
    }

    /// <summary>
    /// Stops and disposes the emulator container.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }
}
