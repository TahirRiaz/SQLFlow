---
id: flow-trl
title: "Translate flow (flowType: trl): query results to shaped JSON, saved and delivered"
type: flow-reference
summary: "flowType: trl maps a SQL query result through a declared JSON template into arbitrarily shaped documents, always saves them to a destination first (deterministic overwrite), and optionally delivers exactly the saved documents to a remote HTTP API."
keywords:
  - translate
  - trl
  - json template
  - document mapping
  - osdu
  - api delivery
  - envelope
  - forEach
  - datasets
  - jsonl
related:
  - flow-overview
  - flow-exp
  - flow-api
  - flow-schedule
  - concept-connections-and-secrets
sourceRefs:
  - src/SqlFlow.Core/Translate/TranslateFlow.cs
  - src/SqlFlow.Core/Translate/TranslateTemplate.cs
  - src/SqlFlow.Core/Translate/TranslateTemplateText.cs
  - src/SqlFlow.Translate/TranslateFlowRunner.cs
  - src/SqlFlow.Translate/JsonTemplateRenderer.cs
  - src/SqlFlow.Translate/TranslateDatasetIndex.cs
  - src/SqlFlow.Translate/TranslateOutputWriter.cs
  - src/SqlFlow.Translate/TranslateInvoker.cs
  - src/SqlFlow.Yaml/YamlTranslateFlowLoader.cs
---

# Translate flow (flowType: trl)

A `trl` flow is a dynamic translation layer: it reads a SQL Server query, maps each row (or the whole result set) through a declared JSON template into documents of ANY shape, writes those documents to a destination, and can then deliver them to a remote HTTP API. The template dialect is fully generic (arbitrary nesting, arrays driven by child datasets, typed leaves, constants), so one flow kind covers any target schema: an OSDU record, a partner ingest payload, a webhook body.

The run has two strictly ordered phases:

1. **Save.** Every document is rendered and written to `output.path` with deterministic names, overwriting the previous run, so the destination always mirrors the latest translation.
2. **Deliver (optional).** The `invoke:` step reads the saved files back and posts their content. What goes on the wire is byte-for-byte what sits on disk, and a failed delivery leaves the files in place for a re-run to re-post.

## Minimal example

```yaml
flowType: trl
name: osdu-dataset-translate
connections:
  dwh: ${env:SQLFLOW_DW}
source:
  server: dwh
  query: SELECT DatasetId, FileName, FileSizeBytes FROM DW.edw.DatasetFile
template:
  id: "kolumbus:dataset--File.Generic:{DatasetId}"
  kind: osdu:wks:dataset--File.Generic:1.1.0
  data:
    Name: "{FileName}"
    TotalSize: { $column: FileSizeBytes, $type: string }
output:
  path: ./out/osdu/dataset
  mode: filePerDocument
  fileName: "dataset_{DatasetId}"
```

Full sample (with child datasets, the OSDU records envelope, and bearer auth): samples/translate/osdu-dataset-translate.flow.yaml.

## The template dialect

The `template:` block mirrors the target JSON. Its keys are the TARGET document's property names; only `$`-prefixed keys are dialect, so any property name a schema uses (GeoJSON's `type`, OSDU's `kind`) is authorable without collision.

| Authored | Emits |
| --- | --- |
| a mapping with no `$` keys | a JSON object, property names verbatim, declaration order preserved |
| a YAML sequence | a fixed JSON array (each element its own template node) |
| `"{Column}"` (exactly one token) | the column's native JSON value: numbers stay numbers, booleans stay booleans, date/time values become ISO 8601 strings, binary becomes base64 |
| a scalar with tokens, e.g. `"urn:x:{Id}"` | a string with each `{Column}` substituted (invariant culture) |
| any other scalar (`text`, `42`, `true`) | a constant, emitted verbatim |

Escapes: `{{` and `}}` are literal braces; a `${...}` sequence passes through verbatim (it is a secret reference, resolved elsewhere, never a column token).

### Directives

A mapping that carries `$` keys is a directive node (mixing `$` keys with plain property names fails at parse):

- `$column: Name` emits the column's value. `$value: <anything>` emits a constant of any YAML shape verbatim (also the escape hatch for literal `$`-prefixed property names). `$template: "text {Col}"` emits a rendered string. Exactly one of the three per node.
- `$type: auto|string|int|long|double|decimal|bool|date|dateTime|json` coerces the value (invariant culture). `json` parses a string column AS JSON and embeds the structure, so a stored GeoJSON fragment lands as an object, not an escaped string. `$format:` applies a .NET format string to `date`/`dateTime`.
- `$whenNull: omit|null|default` overrides the document-level `documents.nulls` for one leaf; `default` emits the `$default:` constant (declaring `$default` alone implies it). A `$template` whose referenced columns are ALL null follows the null policy instead of emitting the bare literal skeleton.
- `$forEach: <dataset>` + `$item: <node>` emits a data-driven array (the REPEATER): one element per dataset row whose `bind` columns match the enclosing scope. The item renders with that row pushed onto the scope, so it sees its own columns first and the enclosing rows' columns behind them; nesting `$forEach` inside `$forEach` chains scopes naturally. At `documents.per: resultSet`, the reserved name `rows` iterates the primary query's rows.
- `$row: <dataset>` + `$item: <node>` is the single-instance counterpart (the HEADER / one-to-one block): the dataset must resolve to exactly one row at the enclosing scope, and the item renders once in that row's scope. Zero or several matching rows fail the run with the count, so a broken header query can never silently emit a wrong document. `$row: rows` wraps the single primary row at result-set grain.

### Datasets

```yaml
datasets:
  - name: owners
    query: SELECT DatasetId, Principal FROM DW.edw.DatasetAcl WHERE AclRole = 'owner'
    bind: [DatasetId]
```

Each dataset query runs once per flow run; its rows are grouped by the `bind` columns and matched to the enclosing scope's same-named columns. Key values compare by a type-normalized invariant form (an `int` matches a `bigint`; strings compare case-insensitively, matching the default SQL Server collation). A bind column the dataset query does not return fails the run naming the missing columns; a bind column absent from the enclosing scope fails naming the columns actually in scope.

A dataset with NO `bind` is single-instance: it is not keyed to any scope and resolves to all of its rows anywhere. That is the header/transactional split made first-class: a one-row bind-less dataset feeds a `$row` header block, a bind-less list feeds a global `$forEach` repeater, and the classic envelope (one file per run: header metadata plus every transaction) is a result-set-grain document whose header is a `$row` over a bind-less dataset and whose transactions are `$forEach: rows`:

```yaml
datasets:
  - name: meta
    query: SELECT SYSUTCDATETIME() AS ExtractedUtc, COUNT(*) AS RecordCount FROM edw.Trip
documents: { per: resultSet }
template:
  header:
    $row: meta
    $item: { extractedAt: {$column: ExtractedUtc, $type: dateTime}, recordCount: "{RecordCount}" }
  transactions:
    $forEach: rows
    $item: { tripId: "{TripId}" }
```

## Documents

```yaml
documents:
  per: row        # row (default) | resultSet
  nulls: omit     # omit (default) | include
```

`per: row` renders one document per primary row and streams (rows are never all buffered). `per: resultSet` renders exactly one document for the whole result, with no columns in scope at the root; the rows are addressed via `$forEach: rows`.

## Output

```yaml
output:
  path: ./out/osdu/dataset   # local path, file:// URI, or Azure Blob/ADLS URI
  mode: filePerDocument      # filePerDocument | jsonLines (default) | array
  fileName: "dataset_{DatasetId}"
  # addTimestamp: true
  # indent: true
```

The destination registry is the export flow's: a plain path stays local, an Azure storage URI writes to the lake under the shared credential (`SQLFLOW_AZURE_AUTH`), selected by CanHandle. Layouts:

- `filePerDocument`: one `<fileName>.json` per document. `{Column}` tokens (per-row grain only) render per document with filesystem-hostile characters sanitized; a rendered name collision fails the run rather than silently overwriting a sibling document; a tokenless name gets the document number appended.
- `jsonLines`: one `<fileName>.jsonl`, one compact document per line (the default; the lake-friendly layout).
- `array`: one `<fileName>.json` holding a single JSON array.

Deterministic overwrite is the contract: no timestamp by default, and the single-file layouts write their file even for zero documents, because "the current result is empty" is itself the state the destination must reflect. `addTimestamp: true` opts a single-file layout into side-by-side runs instead.

## Delivery (invoke)

```yaml
invoke:
  url: https://osdu.example.com/api/storage/v2/records
  method: PUT
  headers:
    data-partition-id: kolumbus
  envelopeKey: records
  auth:
    type: bearer
    secretRef: ${env:OSDU_TOKEN}
  reliability:
    skipStatusCodes: [409]
```

The delivery step reuses the acquisition engine's HTTP stack wholesale: `auth:` takes the exact same surface as an api flow's `source.auth` (api key header/query, bearer, basic, OAuth2 client credentials / refresh token, generic token exchange with OIDC discovery), and `reliability:` the same surface as `source.reliability` (timeout, bounded retry with backoff honoring Retry-After, rate limiting, TLS verification toggle, URL allowlist over the always-on SSRF guard, response-size cap). One deliberate difference: `concurrency` defaults to 1 here, so documents deliver in order unless the flow opts into parallel requests.

Batching follows the saved layout: per-document files post one request per file (or `batchSize` groups them into JSON arrays), a JSON Lines file posts its lines in batches of `batchSize`, and an array file posts as one request holding the whole saved array. `envelopeKey: records` wraps each request's documents as `{"records": [...]}` (always an array, so the endpoint sees one stable shape regardless of batch fill). `{Column}` tokens in the URL need exactly one document and its row per request: per-document output at per-row grain with `batchSize: 1`.

A status listed in `reliability.skipStatusCodes` marks that one request a tolerated skip (counted and logged with the endpoint's response); any other non-retryable failure fails the run, reporting how many requests had already been delivered. A run that produced zero documents sends nothing for the per-document layouts; an array layout still posts its (empty) saved array, since the saved file is the current truth.

## Runs, catalog, and lineage

`run.json` records `flowKind: trl` with total rows, documents, per-file counts/bytes (the catalog's per-run file drill-down reads them), requests delivered and skipped, and the full SQL trace (the primary query and every dataset query land in `trace.sql`). Lineage extracts table-level reads from the declared SQL through the same T-SQL extractor as authored hook scripts, records the output folder as a produced file drop (so a downstream file flow watching it binds in the graph), and records the invoke URL as the flow's outbound endpoint. Run parameters (backfill window, full load, file pattern) do not apply to a translation; supplied ones are acknowledged in the run log and the flow runs as defined.

## Validation guarantees

Everything checkable without a database fails at `validate`, not mid-run: an unknown `$` directive (with the known set listed), mixing directives and plain keys, `$forEach`/`$row` over an undeclared dataset (declared names listed), combining `$forEach` with `$row`, either without `$item`, `rows` outside result-set grain, a dataset named `rows`, `$value` combined with coercion/null directives, `$format` on a non-temporal type, `$whenNull: default` without `$default`, missing `output`, URL/fileName tokens in layouts that have no per-document row scope, `batchSize` on an array output, indentation on JSON Lines, and 2xx entries in `skipStatusCodes`.
