using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using F23.Aspire.Hosting.AzureSearchEmulator;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureSearchEmulator.Aspire.Tests;

public class AzureSearchEmulatorResourceExtensionsTests
{
    [Fact]
    public async Task AddAzureSearchEmulator_ShouldAddResourceToBuilder()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        const string resourceName = "my-emulator";

        // Act
        var resource = builder.AddAzureSearchEmulator(resourceName);

        // Assert
        Assert.NotNull(resource);
        Assert.Equal(resourceName, resource.Resource.Name);
        Assert.Contains(resource.Resource, builder.Resources.ToList());

        var http = resource.Resource.GetEndpoint("http");
        Assert.NotNull(http);
        Assert.Equal(AzureSearchEmulatorResource.DefaultHttpPort, http.TargetPort);
        Assert.Equal("http", http.Scheme);

        var https = resource.Resource.GetEndpoint("https");
        Assert.NotNull(https);
        Assert.Equal(AzureSearchEmulatorResource.DefaultHttpsPort, https.TargetPort);
        Assert.Equal("https", https.Scheme);

        // Publish, not Run: under Run the builder resolves endpoint-backed values through
        // IValueProvider, which blocks forever without a live app host. Publish captures the
        // configured variables without resolving them, which is what this assertion needs.
        var executionConfiguration = await ExecutionConfigurationBuilder
            .Create(resource.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                NullLogger.Instance,
                TestContext.Current.CancellationToken);

        Assert.Contains(executionConfiguration.EnvironmentVariables, kvp => kvp.Key == "ASPNETCORE_URLS");
    }

    [Fact]
    public async Task AddAzureSearchEmulator_ConfiguresKestrelCertificateForHttpsEndpoint()
    {
        // ASPNETCORE_URLS binds an HTTPS endpoint, so Kestrel has to be told which certificate to
        // present. WithHttpsDeveloperCertificate only declares *which* certificate the resource
        // should use -- Aspire provisions the files but applies no configuration of its own for a
        // plain container, so it must be paired with a WithHttpsCertificateConfiguration callback
        // that surfaces the paths. Without one the emulator fails to start with "No server
        // certificate was specified" and every request fails the TLS handshake with an EOF.
        //
        // The callback is invoked directly rather than through ExecutionConfigurationBuilder:
        // resolving a built configuration also resolves ASPNETCORE_URLS, whose endpoint bindings
        // never resolve without a live app host.

        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var resource = builder.AddAzureSearchEmulator("my-emulator").Resource;

#pragma warning disable ASPIRECERTIFICATES001 // Matches the suppression on the call site under test.
        Assert.True(resource.TryGetAnnotationsOfType<HttpsCertificateAnnotation>(out var certificateAnnotations),
            "The resource does not request a certificate for its HTTPS endpoint.");
        Assert.Contains(certificateAnnotations, a => a.UseDeveloperCertificate == true);

        Assert.True(resource.TryGetAnnotationsOfType<HttpsCertificateConfigurationCallbackAnnotation>(out var callbacks),
            "The resource requests a certificate but never configures how to use it; Kestrel will fail to bind the HTTPS endpoint.");

        var environmentVariables = new Dictionary<string, object>();

        // Act
        foreach (var callback in callbacks)
        {
            await callback.Callback(new HttpsCertificateConfigurationCallbackAnnotationContext
            {
                ExecutionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                Resource = resource,
                Arguments = [],
                EnvironmentVariables = environmentVariables,
                CertificatePath = ReferenceExpression.Create($"/certs/cert.crt"),
                KeyPath = ReferenceExpression.Create($"/certs/cert.key"),
                CertificateWithKeyPath = ReferenceExpression.Create($"/certs/cert.pem"),
                PfxPath = ReferenceExpression.Create($"/certs/cert.pfx"),
                Password = null,
                CancellationToken = TestContext.Current.CancellationToken,
            });
        }
#pragma warning restore ASPIRECERTIFICATES001

        // Assert
        Assert.Equal("/certs/cert.crt",
            await ResolveAsync(Assert.Contains("Kestrel__Certificates__Default__Path", environmentVariables)));
        Assert.Equal("/certs/cert.key",
            await ResolveAsync(Assert.Contains("Kestrel__Certificates__Default__KeyPath", environmentVariables)));
    }

    private static async Task<string?> ResolveAsync(object value)
    {
        var valueProvider = Assert.IsAssignableFrom<IValueProvider>(value);

        return await valueProvider.GetValueAsync(TestContext.Current.CancellationToken);
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void WithIndexesVolume_ShouldAddVolumeToResource(bool isReadOnly)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddAzureSearchEmulator("my-emulator");

        // Act
        var updatedBuilder = resourceBuilder.WithIndexesVolume(isReadOnly: isReadOnly);

        // Assert
        Assert.NotNull(updatedBuilder);

        if (!updatedBuilder.Resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out var mountAnnotations))
        {
            Assert.Fail("No mount annotations found on the resource.");
        }

        var mount = mountAnnotations.FirstOrDefault(ma => ma.Target == "/app/indexes");
        Assert.NotNull(mount);
        Assert.Equal(isReadOnly, mount.IsReadOnly);
    }
}
