using System.Xml.Linq;
using F23.Aspire.Hosting.AzureSearchEmulator;

namespace AzureSearchEmulator.Aspire.Tests;

public class AzureSearchEmulatorResourceTests
{
    [Fact]
    public void Constructor_InitializesResourceProperly()
    {
        // Arrange
        const string name = "my-emulator";

        // Act
        var resource = new AzureSearchEmulatorResource(name);

        // Assert
        Assert.Equal(name, resource.Name);
    }

    [Fact]
    public void ImageTag_MatchesPackageVersion()
    {
        // The emulator image and this package are released together, so the pinned tag has to be a
        // tag the release actually publishes. The Docker workflow tags images from the git tag via
        // `type=semver,pattern={{version}}`, so `v1.2.3` publishes the image tag `1.2.3` -- which
        // is the package version. Deriving the tag from the assembly keeps the two in step without
        // a second place to edit at release time; this asserts that derivation stays correct.

        // Arrange
        // Read the version the package actually ships from the csproj, rather than from the
        // assembly the tag is already derived from -- comparing the derivation against its own
        // input would pass no matter what the release version is.
        var csproj = Path.Combine(GetRepositoryRoot(),
            "AzureSearchEmulator.Aspire", "AzureSearchEmulator.Aspire.csproj");

        var packageVersion = XDocument.Load(csproj)
            .Descendants("Version")
            .Single()
            .Value
            .Trim();

        // Act
        var tag = AzureSearchEmulatorResource.ImageTag;

        // Assert
        Assert.Equal(packageVersion, tag);

        // The SDK appends "+<commit sha>" to the informational version, which is not part of any
        // published image tag.
        Assert.DoesNotContain('+', tag);
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AzureSearchEmulator.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory.FullName;
    }
}
