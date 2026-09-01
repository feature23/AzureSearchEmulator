var builder = DistributedApplication.CreateBuilder(args);

// For local development and testing, we add an Azure Search Emulator instance based on the project directly
builder.AddProject<Projects.AzureSearchEmulator>("emulator-project")
    .WithExternalHttpEndpoints();

// Example container usage via F23.Aspire.Hosting.AzureSearchEmulator
builder.AddAzureSearchEmulator("emulator-container")
    // The package pins the image tag to its own version, but that image is not published until the
    // release is tagged -- so in this repo the pinned tag does not exist yet and the pull fails.
    // The demo tracks the code in the working tree rather than a release, so "latest" is the right
    // tag here. Consumers of the package should leave the pinned default alone.
    .WithImageTag("latest")
    .WithIndexesVolume();

builder.Build().Run();
