# Index aliases

An alias is a service-level name that document operations resolve to one index, so a client can
address a stable name while the index behind it is rebuilt and swapped.

## Storage

One JSON file per alias, `<lower-cased name>.alias.json`, in `IndexesDirectory` beside the index
definitions — the same layout as synonym maps (`FileAliasRepository`, behind `IAliasRepository`).
The same guard on `.`, `/` and `\` in a name applies, since the name comes from an untrusted
route. Properties the emulator does not model are kept in `AdditionalProperties`.

```json
{ "name": "live", "indexes": ["products-v2"], "@odata.etag": "\"0x8DE1F2A3B4C5D6E\"" }
```

## Routes

Available with any `api-version`. Aliases are GA in Azure from `2026-04-01`, and in the
previews before it.

`PUT` returns the alias whether or not `Prefer: return=representation` is sent.

| Route | Behaviour |
|---|---|
| `GET /aliases` | `{ "value": [ ... ] }` |
| `POST /aliases` | create; `201`, `409` if it exists |
| `PUT /aliases('x')`, `/aliases/x` | create-or-update; `201` or `200` |
| `GET /aliases('x')`, `/aliases/x` | `200` or `404` |
| `DELETE /aliases('x')`, `/aliases/x` | `204` or `404` |

Responses are written with System.Text.Json by a plain `ControllerBase`, like `/synonymmaps`.

## ETags

Every create or update gives the alias a new ETag, returned in `@odata.etag` and the `ETag`
header. `PUT` and `DELETE` honour `If-Match` (the ETag, or `*` for any existing alias) and
`If-None-Match` (`*` to create only), answering `412` when the condition fails.

## Validation (`400` unless stated)

* Name follows Azure's index naming rules: 2 to 128 characters; lower-case letters, digits, `-`
  and `_`; starts with a letter or digit; no `--` or `__`. Lookups by URL ignore case.
* `indexes` has exactly one entry, and that entry is an existing index.
* The name is not the name of an existing index.
* On `PUT`, the body name matches the URL name.
* Creating an index whose name is held by an alias is refused with `409`.

Re-pointing an alias (`PUT` with a different target) is allowed. Deleting an index an alias
targets is refused with `400`, as in Azure, until the alias is deleted or re-pointed.

## Resolution

`IIndexResolver` looks the name up as an index, then as an alias. The document routes — search
(GET and POST), get, `$count`, suggest, autocomplete and indexing — use it, as do `GET` and `PUT`
on `/indexes('x')`, matching Azure's rule that an alias works for document operations and for
getting and updating an index definition:

* `GET /indexes('alias')` returns the target's definition, under the target's name.
* `PUT /indexes('alias')` updates the target. The body may name the alias or the target; nothing
  is created under the alias's name.
* `DELETE /indexes('alias')` does not resolve the alias and answers `404`, as Azure refuses to
  delete an index through an alias.

An alias left dangling by hand-editing the directory resolves to nothing and gets the same `404`
as a missing index.

## Quota

`/servicestats` reports `aliasesCount.usage` as the number of stored aliases against a quota of
30, twice the index quota of 15, as Azure sets it for services created after October 2022. The
quota is reported, not enforced, as for synonym maps.

## Errors

Plain-text bodies naming the alias or index, as the synonym map routes do.

## Not covered

* Aliases targeting more than one index (Azure does not allow it either).

## Tests

`AliasTests` (unit: validation, file storage, resolver) and `AliasIntegrationTests` (CRUD,
validation, naming rules, ETag preconditions, resolution through the SDK's clients for document
and index routes, re-pointing, refusing to delete a targeted index, `/servicestats`).
