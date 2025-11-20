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
}
