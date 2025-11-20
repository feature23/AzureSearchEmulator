var builder = DistributedApplication.CreateBuilder(args);

// For local development and testing, we add an Azure Search Emulator instance based on the project directly
builder.AddProject<Projects.AzureSearchEmulator>("emulator-project")
    .WithExternalHttpEndpoints();

// Example container usage via F23.Aspire.Hosting.AzureSearchEmulator
builder.AddAzureSearchEmulator("emulator-container")
    .WithIndexesVolume();

builder.Build().Run();
