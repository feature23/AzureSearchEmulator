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

## HTTPS certificates

The emulator serves HTTPS using your machine's ASP.NET Core development certificate, which Aspire
provisions into the container. Most .NET installs already have one; if you have never trusted it,
do so once:

```bash
dotnet dev-certs https --trust
```

The Azure Search SDK then connects with no certificate workarounds — you do **not** need to pass a
custom transport or set `ServerCertificateCustomValidationCallback`:

```csharp
var client = new SearchIndexClient(endpoint, new AzureKeyCredential("any-key"));
```

Earlier versions of this package served a self-signed certificate baked into the emulator image and
required disabling certificate validation. That is no longer necessary, and any such workaround can
be removed.

## Contributing

Submit issues or Pull Requests to us at our GitHub repo! 
https://github.com/feature23/azuresearchemulator
