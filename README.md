# Azure Search Emulator
A local emulator for Azure (Cognitive) Search Service.

This project is currently a prototype, with work underway to validate it in various real-world scenarios 
to ensure that it accurately emulates Azure Search as best as possible.

## Quick Start

1. Clone the repo
2. Open AzureSearchEmulator.sln in Visual Studio 2022 and run it, 
or cd to the `AzureSearchEmulator` folder and run `dotnet run` from the command-line.

## Features

This project aims to be a nearly complete, API-compatible emulator of Azure Search for your local development environment,
offline use, or any dev/test scenario where using a cloud instance of Azure Search is impossible, impractical, or infeasible.
This application is *not* intended for use in production or to replace Azure Search production workloads.

There is another [azure-search-emulator](https://github.com/tomasloksa/azure-search-emulator) project that may or may not be a better
fit for your needs, depending on what you're trying to do. Compared to that project, this project:

* Has no external service/runtime dependencies beyond .NET 6
* Can be run and debugged simply with F5 in Visual Studio, or `dotnet run` on the command line
* Does not require Docker or any kind of containers/virtualization
* Does not require Solr (or Java), Docker Compose, or any kind of orchestration
* Supports index management APIs (creation and deletion at this time)

However, this project may lag behind the other project in some features due to implementing all functionality from scratch.

Currently, there is support (to varying degrees) for the following Azure Search REST APIs:
* Get indexes (multiple index support)
* Create an index
* Delete an index
* Bulk document indexing and deletion (merge, upload, mergeOrUpload, delete)
* Retrieve an individual document
* Get `$count` of all documents
* Search with support for the following parameters: 
  * `$count`
  * `$skip`
  * `$top`
  * `$filter`
  * `search`
  * `queryType`
  * `searchFields`
  * `searchMode`

Metadata about indexes are stored as JSON files in the `indexes` folder. 
Once documents have been added, a subfolder with the index name is created where the Lucene.net index data is stored.
This uses the SimpleFSDirectory Lucene.net directory class to manage its data.

## License

To help ensure the non-production-use of this code, this project uses an [AGPL license](LICENSE). This requires releasing the
source code of your application under a compatible license if this is used in production as a service. 
There is *no* requirement to release your source code if this application is used as intended, as a local emulator for development purposes.
