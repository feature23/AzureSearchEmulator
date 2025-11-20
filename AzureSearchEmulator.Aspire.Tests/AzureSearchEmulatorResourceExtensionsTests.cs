using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using F23.Aspire.Hosting.AzureSearchEmulator;

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

        var envVars = await resource.Resource.GetEnvironmentVariableValuesAsync();
        Assert.True(envVars.ContainsKey("ASPNETCORE_URLS"));
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
