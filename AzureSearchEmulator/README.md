# Azure Search Emulator

A local emulator for Azure AI (previously Cognitive) Search, packaged as a .NET tool.

Run it on your machine for local development, offline work, or any dev/test scenario where a
cloud instance of Azure Search is impossible, impractical, or infeasible. It is *not* intended
for production use.

## Install

```bash
dotnet tool install --global AzureSearchEmulator
```

Or into a single project, using a
[tool manifest](https://learn.microsoft.com/en-us/dotnet/core/tools/local-tools-how-to-use):

```bash
dotnet new tool-manifest
dotnet tool install AzureSearchEmulator
```

## Run

```bash
azsearchemu
```

This listens on <http://localhost:5123> and stores its data in an `indexes` folder in the
directory you ran it from — so each project keeps its own set of indexes by running the tool
from the project folder.

To use a different port or location:

```bash
azsearchemu --urls http://localhost:8080 --Emulator:IndexesDirectory /path/to/indexes
```

With a local tool manifest, prefix the command with `dotnet tool run`:

```bash
dotnet tool run azsearchemu
```

## Connecting the Azure Search SDK

The `Azure.Search.Documents` clients reject `http://` endpoints in their constructors:

```
System.ArgumentException: endpoint only supports https. (Parameter 'endpoint')
```

So to use the SDK against the emulator, run it over HTTPS:

```bash
azsearchemu --urls https://localhost:5123
```

This uses your machine's ASP.NET Core development certificate, which most .NET installs already
have. If you have never trusted it, do so once:

```bash
dotnet dev-certs https --trust
```

The SDK then connects with no certificate workarounds — no
`ServerCertificateCustomValidationCallback`, no custom transport:

```csharp
var endpoint = new Uri("https://localhost:5123");
var client = new SearchIndexClient(endpoint, new AzureKeyCredential("any-key"));
```

Any API key is accepted; the emulator does not authenticate requests.

## What is emulated

Index management, document indexing, and search — including filters, facets, ordering,
highlighting, suggesters and autocomplete, scoring profiles, analyzers, normalizers, synonym
maps, geospatial queries, complex types, and vector search with hybrid ranking.

For the full list of supported REST APIs and search parameters, along with the behavioural
differences from the real service, see the
[project README](https://github.com/feature23/azuresearchemulator#features).

## Other ways to run it

The emulator also runs [in Docker](https://github.com/feature23/azuresearchemulator#building-and-running-with-docker),
or under Aspire via the
[F23.Aspire.Hosting.AzureSearchEmulator](https://www.nuget.org/packages/F23.Aspire.Hosting.AzureSearchEmulator)
package.

## Contributing

Submit issues or Pull Requests to us at our GitHub repo!
https://github.com/feature23/azuresearchemulator
