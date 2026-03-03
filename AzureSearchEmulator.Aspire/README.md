# Azure Search Emulator hosting for Aspire

This package adds Aspire hosting support for the [Azure Search Emulator by feature[23]](https://github.com/feature23/azuresearchemulator).

## Usage

In most cases, you'll probably want to persist your index volume.
Add the following to your AppHost Program.cs:

```csharp
var search = builder.AddAzureSearchEmulator("search")
    .WithIndexesVolume();
```

You can leave off the `WithIndexesVolume()` if you want your search index data to be transient.

## Disable HTTPS Certificate Validation

The emulator runs with a self-signed cert. 
You will need to update your use of the Azure Search SDK to disable HTTPS certificate validation:

```csharp
var options = new SearchClientOptions
{
    Transport = new HttpClientTransport(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    }),
};
```

Pass this `options` object as the last parameter to the constructor of `SearchClient`, `SearchIndexClient`, etc.:

```csharp
var client = new SearchClient(new Uri(searchServiceEndpoint), indexName, new DefaultAzureCredential(), options);
```

## Contributing

Submit issues or Pull Requests to us at our GitHub repo! 
https://github.com/feature23/azuresearchemulator
