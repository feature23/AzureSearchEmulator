# Index aliases

An alias is a service-level name that document operations resolve to one index, so a client can
address a stable name while the index behind it is rebuilt and swapped.

## Storage

One JSON file per alias, `<lower-cased name>.alias.json`, in `IndexesDirectory` beside the index
definitions — the same layout as synonym maps (`FileAliasRepository`, behind `IAliasRepository`).
The same guard on `.`, `/` and `\` in a name applies, since the name comes from an untrusted
route. Properties the emulator does not model are kept in `AdditionalProperties`.

```json
{ "name": "live", "indexes": ["products-v2"], "@odata.etag": null }
```

## Routes

Available with any `api-version`; Azure serves aliases from `2025-11-01-preview`.

| Route | Behaviour |
|---|---|
| `GET /aliases` | `{ "value": [ ... ] }` |
| `POST /aliases` | create; `201`, `409` if it exists |
| `PUT /aliases('x')`, `/aliases/x` | create-or-update; `201` or `200` |
| `GET /aliases('x')`, `/aliases/x` | `200` or `404` |
| `DELETE /aliases('x')`, `/aliases/x` | `204` or `404` |

Responses are written with System.Text.Json by a plain `ControllerBase`, like `/synonymmaps`.

## Validation (`400` unless stated)

* Name present; starts with a letter or digit; letters, digits, `-` and `_`; at most 128 characters.
* `indexes` has exactly one entry, and that entry is an existing index.
* The name is not the name of an existing index.
* On `PUT`, the body name matches the URL name.
* Creating an index whose name is held by an alias is refused with `409`.

Re-pointing an alias (`PUT` with a different target) is allowed. Deleting an index an alias
targets is allowed, leaving the alias dangling.

## Resolution

`IIndexResolver` looks the name up as an index, then as an alias. The document routes — search
(GET and POST), get, `$count`, suggest, autocomplete and indexing — use it. A dangling alias
resolves to nothing and gets the same `404` as a missing index. The `/indexes` routes address
indexes only and never resolve aliases, as in Azure.

## Quota

`/servicestats` reports `aliasesCount.usage` as the number of stored aliases against the existing
quota of 30. The quota is reported, not enforced, as for synonym maps.

## Errors

Plain-text bodies naming the alias or index, as the synonym map routes do.

## Not covered

* ETag / `If-Match` concurrency.
* Aliases targeting more than one index (Azure does not allow it either).

## Tests

`AliasTests` (unit: validation, file storage, resolver) and `AliasIntegrationTests` (CRUD,
validation, resolution through the SDK's `SearchClient`, re-pointing, dangling alias,
`/servicestats`).
