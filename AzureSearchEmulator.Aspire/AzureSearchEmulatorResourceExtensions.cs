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
        /// The image tag defaults to the emulator version matching this package's version. You can
        /// override it by using the returned resource builder's
        /// <see cref="ContainerResourceBuilderExtensions.WithImageTag{T}"/> method.
        /// </remarks>
        public IResourceBuilder<AzureSearchEmulatorResource> AddAzureSearchEmulator(string name,
            int? httpPort = null,
            int? httpsPort = null)
        {
            var resource = new AzureSearchEmulatorResource(name);

#pragma warning disable ASPIRECERTIFICATES001 // WithHttpsDeveloperCertificate is experimental; see comment below.
            var resourceBuilder = builder.AddResource(resource)
                .WithImage(AzureSearchEmulatorResource.ImageName)
                .WithImageTag(AzureSearchEmulatorResource.ImageTag)
                .WithImageRegistry(AzureSearchEmulatorResource.ImageRegistry)
                .WithHttpEndpoint(port: httpPort, targetPort: AzureSearchEmulatorResource.DefaultHttpPort, env: "HTTP_PORTS")
                .WithHttpsEndpoint(port: httpsPort, targetPort: AzureSearchEmulatorResource.DefaultHttpsPort, env: "HTTPS_PORTS")
                .WithEnvironment("ASPNETCORE_URLS", $"https://+:{resource.GetEndpoint("https").Property(EndpointProperty.Port)};http://+:{resource.GetEndpoint("http").Property(EndpointProperty.Port)}")
                // The Azure Search SDK rejects http:// endpoints in its client constructors, so the
                // HTTPS endpoint has to present a certificate the host already trusts. This used to
                // point Kestrel at a certificate baked into the image, which no host had any reason
                // to trust — so SDK calls over that endpoint could never validate. (It had also
                // silently expired in 2022, which went unnoticed because nothing validated it.)
                //
                // Aspire provisions the machine's ASP.NET Core development certificate into the
                // container instead. Because that is the same certificate `dotnet dev-certs https
                // --trust` already installed, SDK calls validate without any bypass, and there is no
                // certificate in the image to go stale. Aspire also reports an actionable message
                // when no trusted development certificate is present, rather than failing the TLS
                // handshake at run time.
                //
                // The API is still marked experimental by Aspire, so the diagnostic is suppressed
                // narrowly here rather than project-wide: if a future Aspire release changes or
                // removes it, this is the one call site to revisit.
                .WithHttpsDeveloperCertificate()
                // WithHttpsDeveloperCertificate only declares which certificate the resource should
                // use; it provisions the files into the container but does not tell the process
                // inside how to find them. Aspire applies no default for a plain container, so
                // without this callback Kestrel is told by ASPNETCORE_URLS to bind HTTPS, finds no
                // certificate, and the host fails to start with "No server certificate was
                // specified" -- every request then fails the TLS handshake with an EOF.
                .WithHttpsCertificateConfiguration(ctx =>
                {
                    ctx.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = ctx.CertificatePath;
                    ctx.EnvironmentVariables["Kestrel__Certificates__Default__KeyPath"] = ctx.KeyPath;

                    return Task.CompletedTask;
                });
#pragma warning restore ASPIRECERTIFICATES001

            return resourceBuilder;
        }
    }

    extension(IResourceBuilder<AzureSearchEmulatorResource> builder)
    {
        /// <summary>
        /// Configures a volume for persisting Azure Search index data.
        /// </summary>
        /// <param name="volumeName">Optional name for the volume. If null, a name will be generated.</param>
        /// <param name="isReadOnly">Indicates whether the volume should be mounted as read-only.</param>
        /// <returns>The resource builder for further configuration.</returns>
        public IResourceBuilder<AzureSearchEmulatorResource> WithIndexesVolume(string? volumeName = null, bool isReadOnly = false)
        {
            return builder.WithVolume(
                name: volumeName ?? VolumeNameGenerator.Generate(builder, "indexes"),
                target: "/app/indexes",
                isReadOnly: isReadOnly);
        }
    }
}
