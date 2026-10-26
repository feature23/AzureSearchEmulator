# Azure Search Emulator
[![.NET](https://github.com/feature23/AzureSearchEmulator/actions/workflows/dotnet.yml/badge.svg)](https://github.com/feature23/AzureSearchEmulator/actions/workflows/dotnet.yml)

A local emulator for Azure AI (previously Cognitive) Search Service.

This project is currently a prototype, with work underway to validate it in various real-world scenarios 
to ensure that it accurately emulates Azure Search as best as possible.

---

**What if your day job was contributing to open-source projects and custom AI solutions &mdash; and you got paid for it?**<br />
We're hiring remote engineers to contribute to cutting-edge AI and custom software projects. 100% remote, 100% real impact. https://www.feature23.com/careers

## Quick Start

The emulator is published as a .NET tool. To install it globally:

```bash
dotnet tool install --global AzureSearchEmulator
```

Then run it from any directory:

```bash
azsearchemu
```

This listens on http://localhost:5123 and stores its data in an `indexes` folder in the
directory you ran it from, so each project can keep its own set of indexes by running the tool
from the project folder. To use a different port or location:

```bash
azsearchemu --urls http://localhost:8080 --Emulator:IndexesDirectory /path/to/indexes
```

### Connecting the Azure Search SDK

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

The SDK then connects with no certificate workarounds:

```csharp
var endpoint = new Uri("https://localhost:5123");
var client = new SearchIndexClient(endpoint, new AzureKeyCredential("any-key"));
```

To install into a single project instead of globally, use a
[tool manifest](https://learn.microsoft.com/en-us/dotnet/core/tools/local-tools-how-to-use):

```bash
dotnet new tool-manifest
dotnet tool install AzureSearchEmulator
dotnet tool run azsearchemu
```

You can also run the emulator [with Docker](#building-and-running-with-docker), or from source:

1. Clone the repo.
2. Open AzureSearchEmulator.sln in Visual Studio 2026, Rider, or Visual Studio Code with C# Dev Kit and run it, 
or cd to the `AzureSearchEmulator` folder and run `dotnet run` from the command-line.

## Web UI

Open the emulator's base address in a browser &mdash; http://localhost:5123 by default &mdash; for a
dashboard showing whether the emulator is running correctly: the version and environment it is
running as, how long it has been up, where it is storing indexes, and the result of each health
check. It refreshes on its own every few seconds.

The emulator is a local development tool, so the UI has no authentication. Do not expose it on an
address other machines can reach.

The same checks are available at `/health`, which answers `Healthy`, `Degraded`, or `Unhealthy` as
plain text with a matching status code, for scripts and container health probes:

```bash
curl http://localhost:5123/health
```

The checks cover the things that go wrong in the environment rather than in the emulator: whether
the indexes directory can actually be written to (a read-only Docker volume, or a directory the
tool was launched from without permission), and whether every index definition on disk still
parses after being hand-edited. A malformed definition reports `Degraded` and names the problem
rather than failing an unrelated request later.

Note that this replaces the OData service document that used to be served at the base address.
Azure AI Search does not serve one either, and the Azure SDK never requests it; `/$metadata` is
unchanged.

## Features

This project aims to be a nearly complete, API-compatible emulator of Azure Search for your local development environment,
offline use, or any dev/test scenario where using a cloud instance of Azure Search is impossible, impractical, or infeasible.
This application is *not* intended for use in production or to replace Azure Search production workloads.

There is another [azure-search-emulator](https://github.com/tomasloksa/azure-search-emulator) project that may or may not be a better
fit for your needs, depending on what you're trying to do. Compared to that project, this project:

* Has no external service/runtime dependencies beyond .NET 10
* Can be run and debugged simply with F5 in Visual Studio, or `dotnet run` on the command line
* Does not require Docker or any kind of containers/virtualization, but can be run with Docker if you prefer (see below)
* Does not require Solr (or Java), Docker Compose, or any kind of orchestration
* Supports index management APIs (creation and deletion at this time)

However, this project may lag behind the other project in some features due to implementing all functionality from scratch.

Currently, there is support (to varying degrees) for the following Azure Search REST APIs:
* Get indexes (multiple index support)
* Create an index
* Delete an index
* Bulk document indexing and deletion (merge, upload, mergeOrUpload, delete)
* Retrieve an individual document
* Get `$count` of all documents in an index
* Search with support for the following parameters: 
  * `$count` - include a count of document matches
  * `$skip` - paging; skip X records, defaults to 0
  * `$top` - paging; take next X records, defaults to 50
  * `$filter` - OData filter expression to limit results, i.e. `(Type eq 'Comment') or (Type eq 'File')`,
    including the geospatial functions `geo.distance` and `geo.intersects`, i.e.
    `geo.distance(Location, geography'POINT(-122.131577 47.678581)') le 10`, and complex type
    sub-field paths, i.e. `Address/City eq 'Seattle'`
  * `$orderby` - OData sort expression to sort results, i.e. `Type asc,Title desc`, including
    sorting by distance, i.e. `geo.distance(Location, geography'POINT(-122.131577 47.678581)') asc`
  * `$select` - Comma-delimited list of fields to return, i.e. `Id,Name,Address/City`; a path may
    name a complex field to take it whole or reach inside one to take a single sub-field
  * `facet` - Field to compute facet buckets over, repeatable, with optional
    `count`/`sort`/`values`/`interval`/`timeoffset` options, i.e. `facet=Category,count:5`
    (see Faceted search below)
  * `highlight` - Comma-delimited list of fields to highlight, supports optional max highlight count i.e. `Body-10,Title-5`
  * `highlightPreTag` - Start tag to wrap highlighted result text, defaults to `<em>`
  * `highlightPostTag` - End tag to wrap highlighted result text, defaults to `</em>`
  * `queryType` - The type of query parser to use, either `simple` (default) or `full`
  * `scoringProfile` - Name of a scoring profile defined on the index, to tune relevance
    (see Scoring profiles below)
  * `scoringParameter` / `scoringParameters` - Values a scoring profile's functions need,
    in the form `name-value`, i.e. `mylocation--122.2,44.8`
  * `search` - The actual search query text to pass to the query parser
  * `searchFields` - Comma-delimited list of fields to search
  * `searchMode` - The default boolean operator, either `any` (default) or `all`
* Suggestions and autocomplete via `docs/suggest` and `docs/autocomplete` (see Suggesters and
  autocomplete below)
* Language and custom analyzers: every predefined `LexicalAnalyzerName`, plus custom
  `analyzers`, `tokenizers`, `tokenFilters` and `charFilters` defined on the index
  (see Analyzers below)
* Normalizers for filter, facet and sort values: every predefined `LexicalNormalizerName`, plus
  custom `normalizers` defined on the index (see Normalizers below)
* Synonym maps: service-level `synonymmaps` in Solr format, named by a field's `synonymMaps`
  to widen queries at search time (see Synonym maps below)
* Vector search: `Collection(Edm.Single)` fields with `vectorSearch` profiles and algorithms,
  `vectorQueries` with all three metrics and both filter modes, and hybrid search fusing text
  and vector rankings with Reciprocal Rank Fusion (see Vector fields below)
* Get service stats (mostly dummy values)
  * [Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/azureai/azureai-search-document-integration) for example uses servicestats route as a health check endpoint.

Beyond the Azure Search API surface, the emulator also serves a web UI and a health endpoint of its
own (see Web UI above).

### Suggesters and autocomplete

An index may declare `suggesters`, each naming the searchable fields that typeahead queries
draw from:

```json
{
  "name": "products",
  "fields": [ ... ],
  "suggesters": [
    { "name": "sg", "searchMode": "analyzingInfixMatching", "sourceFields": ["Name", "Description"] }
  ]
}
```

`SearchClient.Suggest` returns one entry per matching document, each carrying its
`@search.text` alongside the selected document fields, and supports `search`, `suggesterName`,
`$select`, `$filter`, `$top`, `$orderby`, `fuzzy`, `highlightPreTag`, `highlightPostTag`, and
`minimumCoverage`. `SearchClient.Autocomplete` returns distinct completions as
`text`/`queryPlusText` pairs, in all three `autocompleteMode` values — `oneTerm`, `twoTerms`,
and `oneTermWithContext`. Both are available as `GET` and `POST`.

Search text is split on whitespace and punctuation: every term but the last must match a whole
word, while the last is the word still being typed and matches as a prefix. `$top` defaults to
5 and is capped at 100, as in Azure.

The one place this diverges from Azure Search is ranking. Azure builds suggesters from an edge
n-gram index at index time and ranks suggestions by its own n-gram relevance; the emulator has
no such side index and matches with a prefix query at query time instead. The set of
suggestions is the same, but the *order* of two equally-matching suggestions may differ. Code
that asserts on set membership behaves the same locally; code that asserts on exact ranking may
not.

### Scoring profiles

Indexes support `scoringProfiles` and `defaultScoringProfile`, and searches support
`scoringProfile` and `scoringParameter`/`scoringParameters`. Both halves of a profile work:

* **Field weights** (`text.weights`) multiply how much each searchable field contributes to the
  text match, so a term found in a weighted field outranks the same term found elsewhere. They
  apply to both the `simple` and `full` query types.
* **Scoring functions** boost a document by the value of one of its fields. All four types are
  supported — `magnitude` over a numeric field, `freshness` over `Edm.DateTimeOffset`,
  `distance` over `Edm.GeographyPoint`, and `tag` over `Edm.String` or
  `Collection(Edm.String)` — with all four `interpolation` curves and all six
  `functionAggregation` modes.

Scoring parameters use Azure's `name-value` format, i.e. `mytags-luxury,budget`. A reference
point for a `distance` function is longitude-first, so `mylocation--122.2,44.8` is a single
dash separating the name from a value that begins with a negative longitude.

A profile is validated when the index is created, so a function over a field of the wrong type,
a weight on a non-searchable field, or a `boost` outside the range Azure defines (greater than
1, and here capped at 1,000,000 so a score cannot overflow) is a `400` at definition time
rather than a query that silently does no boosting. A search naming a profile the index does not define, or omitting a
scoring parameter one of its functions needs, is likewise refused rather than answered
unboosted.

Matching Azure, a profile applies to full-text search only. An empty or wildcard search
(`search=*`) is not ranked — every result comes back with a uniform `@search.score` — and
suggest and autocomplete are not scored at all.

**The one thing that differs is the score values themselves.** The emulator ranks with Lucene,
whose relevance implementation is not Azure's, so `@search.score` will not match what the real
service returns even before a profile is applied. What a profile controls, and what does carry
over, is the *relative order* of results: boosting recent documents, or nearby ones, or ones
matching a tag, reorders your results here the same way it does in Azure. Code that asserts on
result ordering behaves the same locally; code that asserts on exact score values does not.

Azure also documents the interpolation curves only qualitatively and never publishes their
formulas, so the exact size of an individual boost is an interpretation of that description
rather than a reproduction of Azure's arithmetic.

### Replica-dependent parameters

The emulator runs a single index, which is the same thing Azure does when a service has one
replica. Parameters that describe how a query is spread across replicas are therefore answered
exactly rather than approximately:

* `minimumCoverage` - accepted at any value. Coverage is the percentage of the index that was
  searched, and a single local index is always fully covered, so any floor you set is
  genuinely met. Requests that supply it get `@search.coverage: 100` back, and — matching
  Azure — requests that do not supply it get no `@search.coverage` field at all.
* `sessionId` - accepted and has no effect. It asks that repeated queries be routed to the
  same replica for consistent scoring, which is trivially true when there is only one.
* `scoringStatistics` - accepted. `local` and `global` differ only in whether term statistics
  are aggregated across replicas before scoring.

One consequence worth knowing: a code path that handles *degraded* coverage will never be
exercised locally, because coverage here is always 100. That is a limit of running a single
replica rather than a difference in the response — Azure with one healthy replica answers the
same way.

### Faceted search

Fields marked `facetable` can be used for faceted navigation. Facets are requested per query
and returned under `@search.facets`, alongside the results:

```
facet=Category
facet=Tags,count:5
facet=Rating,sort:-value
```

Each facet expression is a field path followed by comma-separated options. `count` caps the
number of buckets (default 10; `count:0` means no limit) and `sort` orders them (`count`,
`-count`, `value`, or `-value`, defaulting to descending by count with ties broken by value).

`values` and `interval` turn a numeric or `Edm.DateTimeOffset` facet into ranges instead, with
the bucket bounds reported as `from`/`to`:

```
facet=BaseRate,values:80|150|220        # four buckets, the outermost open-ended
facet=BaseRate,interval:100             # buckets of width 100
facet=LastRenovationDate,interval:year  # one bucket per year
facet=LastRenovationDate,interval:day,timeoffset:-01:00
```

As in Azure Search, `count`/`sort` cannot be combined with `values`/`interval`, `values` and
`interval` cannot be combined with each other, and `timeoffset` only applies to `interval` on a
date field.

Facets are computed over the whole set of matching documents, not just the page being
returned, so `$top` and `$skip` do not change the counts — but `$filter` does, since it changes
what matches. Setting `$top=0` returns the facet structure with no documents, which is the
usual way to populate a navigation panel.

Counts are of *documents*: a hotel with two deluxe rooms counts once toward `Deluxe`, and a
hotel with two rooms in one price band counts once in that bucket. A document does appear in
every bucket it belongs to, so buckets of a `Collection(Edm.String)` field — or of a sub-field
of a `Collection(Edm.ComplexType)` — can sum to more than the number of matching documents.

Sub-fields of complex types are faceted by path, i.e. `facet=Address/City` or
`facet=Rooms/BaseRate`. Matching Azure Search, `Edm.GeographyPoint` fields and complex fields
themselves cannot be faceted, and faceting on a field that is not `facetable` is an error
rather than being silently ignored.

### Geospatial support

Fields of type `Edm.GeographyPoint` can be indexed, filtered, sorted, and retrieved.
As in Azure Search, document values use the GeoJSON `Point` format
(`{ "type": "Point", "coordinates": [longitude, latitude] }`), while filter and `$orderby`
expressions use WKT literals (`geography'POINT(longitude latitude)'`), and `geo.distance`
returns kilometers. Note that both forms list *longitude before latitude*.

`Collection(Edm.GeographyPoint)` is supported for indexing, filtering, and retrieval. As in
Azure Search, a document matches when *any* of its points satisfies the filter, which is
normally written with a lambda, i.e.
`Locations/any(loc: geo.distance(loc, geography'POINT(-122.131577 47.678581)') le 10)`.
Collections cannot be sorted, so `$orderby` still requires a single `Edm.GeographyPoint`
field.

### Analyzers

Every analyzer name in Azure's `LexicalAnalyzerName` list is accepted, along with custom
analyzers the index defines itself.

The `.lucene` language analyzers are backed by the same Apache Lucene analyzers Azure uses, so
those behave as they do in the service. The `.microsoft` names are accepted and mapped to the
closest Lucene analyzer for that language, but Azure implements them with a proprietary
natural-language stack that lemmatizes rather than stems, so the *tokens* will not always match
the service exactly. Chinese, Japanese and Korean are segmented into bigrams; Polish, Thai and
the other languages Lucene has no analyzer for fall back to the standard analyzer, which
segments correctly but does not stem.

Custom analyzers are defined as they are in Azure, with `analyzers`, `tokenizers`,
`tokenFilters` and `charFilters`:

```jsonc
{
  "fields": [
    { "name": "title", "type": "Edm.String", "searchable": true, "analyzer": "my_analyzer" }
  ],
  "analyzers": [
    {
      "name": "my_analyzer",
      "@odata.type": "#Microsoft.Azure.Search.CustomAnalyzer",
      "tokenizer": "standard_v2",
      "tokenFilters": ["lowercase", "asciifolding"],
      "charFilters": ["html_strip"]
    }
  ]
}
```

`#Microsoft.Azure.Search.PatternAnalyzer`, `StandardAnalyzer` and `StopAnalyzer` are supported
alongside `CustomAnalyzer`, and the built-in tokenizers, token filters and char filters can be
given options by defining them under `tokenizers`, `tokenFilters` or `charFilters` and naming
the definition from the chain.

An analyzer name that is neither predefined nor defined on the index is rejected when the index
is created, naming the field and the analyzer, rather than failing later when a document is
indexed.

#### Not supported

* **`microsoft_language_tokenizer` and `microsoft_language_stemming_tokenizer`**, which are the
  proprietary stack and have no Lucene equivalent.
* **The `phonetic` token filter**, which lives in a Lucene package this project does not
  reference.

### Normalizers

Filters, facets and `$orderby` compare whole values rather than analyzed tokens, so by default
`City eq 'las vegas'` does not match a document holding `"Las Vegas"`. A `normalizer` on the
field folds both sides of that comparison the same way, so the variants match, facet into one
bucket and sort together.

Normalizers apply to `Edm.String` and `Collection(Edm.String)` fields that are `filterable`,
`sortable` or `facetable`. They do not affect full-text search — a searchable field's own
analyzer still decides how its tokens are produced — and they do not change what a search
returns, which is always the value the document supplied.

All five predefined normalizers are supported: `standard` (lowercase then asciifolding),
`lowercase`, `uppercase`, `asciifolding` and `elision`.

```jsonc
{
  "fields": [
    {
      "name": "city", "type": "Edm.String",
      "filterable": true, "facetable": true, "sortable": true,
      "normalizer": "lowercase"
    }
  ]
}
```

Custom normalizers are defined as they are in Azure, under `normalizers`, and compose the same
`tokenFilters` and `charFilters` a custom analyzer does. They name no tokenizer, because a
normalizer always produces a single token:

```jsonc
{
  "fields": [
    {
      "name": "city", "type": "Edm.String", "filterable": true,
      "normalizer": "my_normalizer"
    }
  ],
  "normalizers": [
    {
      "name": "my_normalizer",
      "@odata.type": "#Microsoft.Azure.Search.CustomNormalizer",
      "charFilters": ["map_dash"],
      "tokenFilters": ["asciifolding", "lowercase"]
    }
  ],
  "charFilters": [
    {
      "name": "map_dash",
      "@odata.type": "#Microsoft.Azure.Search.MappingCharFilter",
      "mappings": ["-=>_"]
    }
  ]
}
```

Because a normalizer must produce one token, only the filters Azure documents for normalizers
are accepted: the `mapping` and `pattern_replace` char filters, and the `lowercase`,
`uppercase`, `asciifolding`, `elision` and language normalization token filters. Anything that
would split, drop or multiply the token — `ngram`, `stopwords`, `html_strip`, the stemmers — is
rejected when the index is created, as it is by the service.

A normalizer name that is neither predefined nor defined on the index is also rejected at
creation, naming the field and the normalizer. As with an analyzer, a field's normalizer is
fixed once the index exists; changing it requires rebuilding the index.
* **Options that name a file**, such as a synonym map on disk. Word lists and mappings given
  inline in the index definition work normally.

### Synonym maps

A synonym map widens a full-text query so that a search for one term also matches documents
holding an equivalent one, without either the query or the documents being rewritten. Maps are
service-level resources with their own routes, and a field opts in by naming one:

```http
POST /synonymmaps?api-version=2024-07-01
Content-Type: application/json

{
  "name": "products",
  "format": "solr",
  "synonyms": "usa, united states, united states of america\ndog => canine, hound"
}
```

```json
{
  "name": "name", "type": "Edm.String", "searchable": true,
  "synonymMaps": ["products"]
}
```

`GET`, `PUT` and `DELETE` on `/synonymmaps('products')` behave as the service's do, and the map
appears in the `synonymMaps` counter of `/servicestats`.

The Solr format has two rule forms, and they do different things:

* **Equivalency** — `usa, united states` — makes every term match every other, keeping the term
  that was typed. A search for either matches documents holding either.
* **Explicit mapping** — `dog => canine, hound` — replaces the left side with the right, so a
  search for `dog` matches documents holding `canine` or `hound`, but no longer matches
  documents holding only `dog`.

Rules are separated by newlines, matched case-insensitively, and may span several words
(`united states of america`), which match as a phrase.

Expansion happens at query time only, never while indexing — the same as the service. That is
what makes a map safe to edit: the rules a search uses are the ones the map holds now, so
changing them takes effect immediately and never requires reindexing. A field may name several
maps, and each is applied in turn.

Synonym maps apply to `searchable` `Edm.String` and `Collection(Edm.String)` fields. A field
that names a map it cannot use — one that is not searchable, or holds no text — is rejected when
the index is created, as is a field naming a map the service does not have. Only the `solr`
format is supported, which is the only one Azure Search itself supports.

### Filter semantics

Null comparison, lexicographic string ranges, and the two full-text filter functions behave
as they do in Azure Search:

```
$filter=Description eq null                # the field has no value
$filter=Description ne null                # the field has some value
$filter=Name ge 'M' and Name lt 'S'        # lexicographic string range
$filter=search.ismatch('luxury')           # matches, without affecting relevance
$filter=search.ismatchscoring('luxury')    # matches, and contributes to the score
```

A field is null when the document omits it, sends it as JSON `null`, or — for a collection —
supplies an empty array. A complex field is null when every one of its sub-fields is.

String ranges compare **ordinally**, by UTF-8 byte sequence, so every uppercase letter sorts
before every lowercase one and `'Z' lt 'a'` holds. The comparison uses the field's exact
stored value, so an analyzed searchable field still ranges over what the document actually
contains rather than over its lowercased search tokens.

`search.ismatch` filters without contributing to relevance, while `search.ismatchscoring`
selects the same documents and feeds their full-text scores into the ranking.

### Vector fields

Fields of type `Collection(Edm.Single)` can be declared, uploaded, and retrieved. An index
declares its vector configuration under `vectorSearch`, and a vector field names a profile
from it, exactly as in Azure Search:

```json
{
  "name": "embedding",
  "type": "Collection(Edm.Single)",
  "searchable": true,
  "retrievable": false,
  "dimensions": 1536,
  "vectorSearchProfile": "vp"
}
```

```json
"vectorSearch": {
  "algorithms": [
    { "name": "hnswAlgo", "kind": "hnsw",
      "hnswParameters": { "m": 4, "efConstruction": 400, "efSearch": 500, "metric": "cosine" } }
  ],
  "profiles": [ { "name": "vp", "algorithm": "hnswAlgo" } ]
}
```

Both algorithm kinds (`hnsw` and `exhaustiveKnn`) and all three metrics (`cosine`, the
default, plus `dotProduct` and `euclidean`) are accepted. The HNSW tuning parameters `m`,
`efConstruction` and `efSearch` are accepted and preserved, but they do not affect results.

Properties the emulator does not model — `vectorizers`, `compressions`, and anything Azure
adds later — are preserved verbatim, so a definition read from the real service survives a
get-modify-put unchanged. The one exception is a **profile-level `vectorizer`**, which is
rejected outright rather than kept: it would make the emulator accept an index whose queries
it must then refuse, and saying so at create time points at the profile responsible.

`dimensions` must be between 2 and 4096, the bounds the Azure REST specification sets. The
`hamming` metric is not supported: Azure restricts it to bit-packed binary vectors, an element
type the emulator does not implement.

A document whose vector length does not match the field's `dimensions` is rejected with a
400, as in Azure Search, without failing the rest of the batch. `dimensions` cannot be
changed once the index exists.

A vector field may be declared inside an `Edm.ComplexType`, but not inside a
`Collection(Edm.ComplexType)`: a document holds one vector per field, so the several vectors
the elements of a collection would contribute have nowhere to go. That combination is
rejected when the index is created.

### Vector queries

A vector field is searched with `vectorQueries`, supplying a precomputed embedding:

```json
{
  "vectorQueries": [
    { "kind": "vector", "vector": [0.1, 0.2, 0.3], "fields": "embedding", "k": 5 }
  ]
}
```

`k` and `kNearestNeighborsCount` are both accepted — the REST reference uses the former, the
Azure SDK sends the latter. Naming no `fields` searches every vector field in the index.
Fields searched together must share a metric, since one query produces one ranking.

The search is **exhaustive**: every document holding a vector is scored and the best `k` are
kept, for both algorithm kinds. Azure reaches the same answer approximately through an HNSW
graph, trading recall for speed at a scale the emulator does not operate at. An exact answer
is deterministic, so a test asserting which documents came back cannot become flaky.

`vectorFilterMode` selects when `$filter` applies:

* `preFilter` (default) — the filter narrows the candidates *before* the neighbours are
  chosen, so a full `k` of matching documents comes back.
* `postFilter` — the filter applies *after* selection, so an excluded document still consumes
  one of the `k` slots and the result can be shorter than `k`.

`k` and `$top` are different knobs: `k` is how many neighbours the vector query contributes,
`$top` is how many of them a page returns.

#### Scores

For `cosine`, `@search.score` follows the formula Azure documents, `1 / (1 + cosine_distance)`
— an exact match scores 1 and an orthogonal vector 0.5.

For `dotProduct` and `euclidean`, Azure confirms that a transform from similarity to score
exists but does not publish it, so **the emulator's scores for those two metrics are its own**.
They are strictly monotonic in the similarity and land in the same `(0, 1]` range, so the
*ordering* is faithful for every metric and the *absolute score* is faithful only for cosine.
Assert on which documents came back and in what order rather than on a literal score, unless
the metric is cosine.

#### Hybrid search

Sending `search` and `vectorQueries` together runs both and fuses the rankings with
Reciprocal Rank Fusion, as Azure does. Each arm contributes `1/(60 + rank)` for every
document it returns, and a document's score is the sum across the arms it appears in — so a
document both arms rate well outranks one that a single arm rates best, which is the reason
to run a hybrid query at all.

The arms cannot simply be unioned: a BM25 score is unbounded and a vector score sits in
`(0, 1]`, so whichever produced larger numbers would decide the ranking regardless of how the
other arm rated the same documents. RRF discards the scores and fuses on rank instead.

**Each *field* is its own arm**, not each vector query. A text query alongside one vector
query naming two fields fuses three rankings. Several vector queries with no `search` are
fused the same way; a single vector query with no `search` is not a fusion and keeps its
similarity score.

`weight` on a vector query scales every term that arm contributes (default `1.0`). The text
arm always has an implicit weight of 1.0 and cannot be given one, matching Azure.

**RRF scores are small by construction.** One arm contributes at most `1/61 ≈ 0.0164`, so a
two-arm hybrid tops out near `0.033`. Azure warns about this directly — a score of 0.03 is a
strong match, not a weak one — and hybrid scores are not comparable with the `@search.score`
of a pure text or pure vector query.

Two details Azure does not document are reproduced here from the scores it publishes: ranks
are **1-based**, and the arithmetic is **single precision**. A document ranked first by both
arms scores exactly `0.032786883413791656`, which is `float32(2/61)` and the value Azure's own
hybrid example reports.

Ties have no documented rule, and Azure explicitly disclaims stable ordering for equal scores.
The emulator breaks them by internal document order, which makes a fused ranking reproducible
for a given index — though not necessarily stable across a reindex, since that order can change
when segments merge.

#### Not supported

* **`kind: "text"` queries and `vectorizers`**, which need a hosted embedding model. Supply
  precomputed embeddings instead.
* **Compression and quantization**, which are accepted in an index definition and ignored;
  the emulator stores vectors uncompressed, which can only make its results more exact.
* **Semantic ranking** (`@search.rerankerScore`), which in Azure layers on top of the fused
  result rather than participating in it.

### Complex type support

Fields of type `Edm.ComplexType` and `Collection(Edm.ComplexType)` can be indexed, filtered,
searched, and retrieved. Sub-fields are declared under a field's `fields` property and are
addressed by a slash-delimited path, exactly as in Azure Search:

```
$filter=Address/City eq 'Seattle'
$orderby=Address/Geo/Lat asc
searchFields=Address/City
```

Complex types may be nested to any depth, and a `Collection(Edm.ComplexType)` may itself
contain primitive collections.

A `Collection(Edm.ComplexType)` is filtered with a lambda, so that a document matches when
one of its elements satisfies the predicate:

```
$filter=Rooms/any(r: r/Type eq 'Deluxe')
$filter=Rooms/any(r: r/Tags/any(t: t eq 'wifi'))
$filter=Rooms/all(r: r/SmokingAllowed eq false)
$filter=Rooms/any()                                  # the collection is non-empty
```

Criteria inside a lambda are **correlated**: they all apply to the same element. So

```
$filter=Rooms/any(r: r/Type eq 'Deluxe' and r/BaseRate lt 100)
```

matches only a hotel with a single room that is both a deluxe *and* under 100 — not one whose
deluxe room is expensive and whose cheap room is a standard. As in Azure Search,
[`any`/`all` over a complex collection accept any filter construct][collection-ops] except
`search.ismatch`/`search.ismatchscoring`, and a lambda body may only reference fields bound to
its own range variable.

Note that the more restrictive rules Azure documents for *primitive* collections still apply
to those — `Collection(Edm.String)`, for instance, allows only `eq`/`search.in` inside `any`
and only `ne`/`not search.in` inside `all`.

One limitation is worth noting, matching Azure Search's own behavior: sub-fields of a
`Collection(Edm.ComplexType)` cannot be sorted on, since a document has one value per element
rather than a single value to order by.

[collection-ops]: https://learn.microsoft.com/en-us/azure/search/search-query-odata-collection-operators#limitations

Metadata about indexes are stored as JSON files in the `indexes` folder. 
Once documents have been added, a subfolder with the index name is created where the Lucene.net index data is stored.
This uses the SimpleFSDirectory Lucene.net directory class to manage its data.

## Authentication

Authentication is not yet implemented. If you're using the Azure Search SDK, you can provide any value for the `AzureKeyCredential` constructor parameter.

## Building and Running with Docker

It is not required to use Docker to run this project, see the Quick Start section above. 

### One-time certificate setup

The image ships no TLS certificate of its own. Because the Azure Search SDK requires HTTPS and
validates the certificate, the container serves your machine's ASP.NET Core development
certificate — the one your host already trusts — mounted in from the host.

Export it once:

```bash
dotnet dev-certs https --trust
dotnet dev-certs https -ep ~/.aspnet/https/aspnetapp.pfx -p devcert
```

`docker-compose.yml` mounts `~/.aspnet/https` and reads the password from `CERT_PASSWORD`,
defaulting to `devcert`. Override either with `CERT_PATH` and `CERT_PASSWORD` if you keep your
certificate elsewhere or use a different password.

### Running with Docker Compose

The easiest way to run with Docker is to use Docker Compose. Run the following from the repo root:

```bash
docker compose up -d
```

This will build the image, create the volume, and run the container in the background at https://localhost:5081 and http://localhost:5080. See the `docker-compose.yml` file for how this works.

If you prefer to do this without Docker Compose (HTTP only):

```bash
# create a volume to persist your indexes across runs
docker volume create az-search-emu

# from repo root
docker build . -t azure-search-emulator

# run the container on port 5080 (feel free to change) and mount the volume
docker run -dp 5080:80 -v az-search-emu:/app/indexes azure-search-emulator
```

## Contributing

Please make sure there is an issue for the feature or bug you are working on before submitting a pull request.
If not, please open an issue first so we can discuss the change.

Make sure all unit tests pass before submitting a pull request.

To run the unit tests, from the repo root run:

```bash
dotnet test
```

If adding a new API or feature, please add appropriate unit tests and DebugClient checks to cover the new functionality.

When creating a pull request, please do so from a branch on your fork, not from main/master.
A good naming convention is to use the issue number, i.e. `issue/123`.

## License

To help ensure the non-production-use of this code, this project uses an [AGPL license](LICENSE). This requires releasing the
source code of your application under a compatible license if this is used in production as a service. 
There is *no* requirement to release your source code if this application is used as intended, as a local emulator for development purposes.
