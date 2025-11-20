using Aspire.Hosting.ApplicationModel;
using F23.Aspire.Hosting.AzureSearchEmulator;

// ReSharper disable once CheckNamespace
namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring Azure Search Emulator resources in Aspire.
/// </summary>
public static class AzureSearchEmulatorResourceExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        /// <summary>
        /// Adds an Azure Search Emulator container resource to the distributed application.
        /// </summary>
        /// <param name="name">The name of the resource.</param>
        /// <param name="httpPort">An optional HTTP port. If null, will use a generated port number.</param>
        /// <param name="httpsPort">An optional HTTPS port. If null, will use a generated port number.</param>
        /// <returns>A resource builder for further configuration.</returns>
        /// <remarks>
        /// It is recommended to configure a volume for persisting index data using
        /// <see cref="WithIndexesVolume"/>.
        /// You can also override the default image tag ("latest") by using the returned resource builder's
        /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}"/> method.
        /// </remarks>
        public IResourceBuilder<AzureSearchEmulatorResource> AddAzureSearchEmulator(string name,
            int? httpPort = null,
            int? httpsPort = null)
        {
            var resource = new AzureSearchEmulatorResource(name);

            var resourceBuilder = builder.AddResource(resource)
                .WithImage("feature23/azuresearchemulator")
                .WithImageTag("latest")
                .WithImageRegistry("ghcr.io")
                .WithHttpEndpoint(port: httpPort, targetPort: AzureSearchEmulatorResource.DefaultHttpPort, env: "HTTP_PORTS")
                .WithHttpsEndpoint(port: httpsPort, targetPort: AzureSearchEmulatorResource.DefaultHttpsPort, env: "HTTPS_PORTS")
                .WithEnvironment("ASPNETCORE_URLS", $"https://+:{resource.GetEndpoint("https").Property(EndpointProperty.Port)};http://+:{resource.GetEndpoint("http").Property(EndpointProperty.Port)}")
                .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Password", "password")
                .WithEnvironment("ASPNETCORE_Kestrel__Certificates__Default__Path", "/app/aspnetapp.pfx");

            return resourceBuilder;
        }
    }

    extension(IResourceBuilder<AzureSearchEmulatorResource> builder)
    {
        /// <summary>
        /// Configures a volume for persisting Azure Search index data.
        /// </summary>
        /// <param name="volumeName">Optional name for the volume. If null, a name will be generated.</param>
        /// <returns>The resource builder for further configuration.</returns>
        public IResourceBuilder<AzureSearchEmulatorResource> WithIndexesVolume(string? volumeName = null)
        {
            return builder.WithVolume(volumeName ?? VolumeNameGenerator.Generate(builder, "indexes"), "/app/indexes");
        }
    }
}
