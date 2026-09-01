using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace F23.Aspire.Hosting.AzureSearchEmulator;

public class AzureSearchEmulatorResource(string name) : ContainerResource(name)
{
    public const int DefaultHttpPort = 5100;
    public const int DefaultHttpsPort = 5143;

    internal const string ImageRegistry = "ghcr.io";
    internal const string ImageName = "feature23/azuresearchemulator";

    /// <summary>
    /// The container image tag used by <see cref="AzureSearchEmulatorResourceExtensions"/>.
    /// </summary>
    /// <remarks>
    /// This is the emulator version that matches this package's version, rather than "latest".
    /// Docker treats "latest" as an ordinary tag, so a host that had already pulled it kept running
    /// the image it pulled the first time and never picked up the emulator that a newer package
    /// expects. Pinning ties the container to the package, so upgrading the NuGet reference pulls
    /// the matching image.
    ///
    /// This is asserted against the package version by AzureSearchEmulatorResourceTests, so
    /// releasing a new version does not need this value updated by hand.
    /// </remarks>
    internal static readonly string ImageTag = typeof(AzureSearchEmulatorResource).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion
        // The SDK appends "+<commit sha>" to the informational version; the published image tag
        // carries only the version itself.
        .Split('+')[0];
}
