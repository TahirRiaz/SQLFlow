---
id: flow-api
title: "API flow (flowType: api): acquire from an HTTP API, SFTP server, or Azure Table"
type: flow-reference
summary: "flowType: api fetches from an HTTP API, SFTP server, or Azure Table and lands raw payloads byte-for-byte, with auth, pagination, fan-out, and resume."
keywords:
  - api
  - acquisition
  - acquire
  - http
  - rest
  - graphql
  - soap
  - pagination
  - iterate
  - landing
  - watermark
  - runbook replacement
related:
  - flow-overview
  - flow-sftp
  - flow-cpy
  - flow-trl
  - concept-landing-data-protection
  - concept-connections-and-secrets
  - concept-file-discovery-and-lifecycle
sourceRefs:
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
  - src/SqlFlow.Yaml/YamlAcquireFlowLoader.cs
  - src/SqlFlow.Acquire/Engine/AcquireEngine.cs
  - src/SqlFlow.Acquire/Engine/AcquireFlowRunner.cs
  - src/SqlFlow.Acquire/Engine/HttpTransport.cs
  - src/SqlFlow.Acquire/Engine/SftpTransport.cs
  - src/SqlFlow.Acquire/Engine/AzureTableTransport.cs
  - src/SqlFlow.Acquire/Engine/LandingPipeline.cs
  - src/SqlFlow.Acquire/Runtime/AuthResolver.cs
  - src/SqlFlow.Acquire/Runtime/RetryPolicy.cs
  - src/SqlFlow.Acquire/Runtime/UrlGuard.cs
  - src/SqlFlow.Acquire/Runtime/TemplateEngine.cs
---

# API flow (flowType: api)

An `api` flow fetches from a third-party system and lands the **raw** response payloads as files in
the data lake, one file per page or iteration, preserving the original format byte-for-byte. It is
the declarative replacement for a hand-written acquisition runbook.

It performs no CSV conversion and no flattening: the existing `json`/`csv`/`xml` file flows ingest
what it lands. The single exception is `landing.protect`, which scrubs declared fields before the
bytes are written (see [Data protection](#data-protection-at-the-landing-boundary)).

Three transports share one document shape. `http` covers REST, GraphQL, and SOAP-over-POST; `sftp`
lists and downloads remote files; `azuretable` queries an Azure Storage Table. The non-HTTP
transports carry their credentials and locators in `source.options`. Transport matching strips `-`
and `_` and is case-insensitive, so `azure_table` and `AzureTable` both parse. S3 object copy is a
[cpy](cpy.md) flow, not an `api` transport.

## Minimal example

```yaml
flowType: api
name: vendor_00_api
batch: api

source:
  transport: http
  baseUrl: https://api.vendor.com
  auth:
    type: bearer
    secretRef: ${keyvault:dw-keyvault-prod/vendor-token}
  request:
    method: GET
    path: /v1/trips
    query:
      from: "{window.from:yyyy-MM-dd}"
      to: "{window.to:yyyy-MM-dd}"
  pagination:
    strategy: offset
    offsetParam: offset
    limitParam: limit
    limit: 500
    recordsPath: $.data
  iterate:
    - kind: date_window
      granularity: daily
      from: now-7d
      to: now
      fromVariable: window.from
      toVariable: window.to
  reliability:
    timeoutSeconds: 120
    rateLimitRps: 5
    urlAllowlist: ["api.vendor.com"]

landing:
  target: abfss://datalakev2@account.dfs.core.windows.net/raw/vendor/api/trips
  pathTemplate: "history/{yyyy}/{MM}/vendor_trips_{window.from:yyyyMMdd}_{page}"
  format: auto
```

```bash
sqlflow validate vendor_00_api.yaml
sqlflow run      vendor_00_api.yaml
```

## The two forms

A flow declares **either** one endpoint or several, never both.

**Single-endpoint form:** `source.request` (plus optional `source.pagination` / `source.iterate`) and
a top-level `landing`.

**Multi-item form:** an `items` list, each entry carrying its own `request`, `pagination`, `iterate`,
and `landing`, over the shared `source` connection envelope (`transport`, `baseUrl`, `auth`,
`reliability`, `options`). One flow fetches every listed endpoint and lands each to its own raw path,
so a single acquisition can feed several downstream file flows with lineage intact.

Putting `request`/`pagination`/`iterate` on `source`, or a top-level `landing`, alongside `items` is
a validation error, as is combining `items` with `incremental`: the run watermark is one value per
run, so multi-item flows express incrementality through each item's date-window `iterate`.

## Top-level keys

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `api`. |
| `name` | string | yes | | Flow identity; seeds the stable flow id and names the run-history folder. Renaming yields a new identity by design. |
| `batch` | string | no | | Grouping label (the source system). |
| `mode` | enum | no | `auto` | `auto` runs on schedule fires and group runs; `manual` reserves it for direct triggers; `disabled` deactivates a retired flow. |
| `params` | map | no | | Declared runtime parameters, name to default. Each becomes a template variable `{name}`. An empty or null default makes the parameter **required**. Supplying an undeclared name fails the run before the first request. |
| `source` | object | yes | | The source side: transport, endpoint, auth, request, pagination, fan-out, reliability. |
| `items` | list | one of | | The multi-endpoint form (see above). |
| `landing` | object | one of | | Where payloads are written (single-endpoint form). |
| `incremental` | object | no | | Resume configuration (single-endpoint form only). |

### source

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `transport` | enum | no | `http` | `http`, `sftp`, or `azuretable`. |
| `baseUrl` | string | yes | | Per transport: an http(s) origin, `sftp://host[:port]`, or the Azure Table account URL. For HTTP, `request.path` is joined under it. |
| `auth` | object | no | | HTTP authentication (ignored by the non-HTTP transports). |
| `request` | object | no | | The HTTP request template. |
| `pagination` | object | no | | The pagination state machine. |
| `iterate` | list | no | | Fan-out iterations. |
| `reliability` | object | no | | Timeout, retry, rate limit, size cap, TLS, SSRF allowlist. |
| `options` | map | no | | Transport-specific options for `sftp` and `azuretable`. |

`source.options` per transport. Secret-bearing values must be `${...}` references, never inline.

- **sftp**: `username` (required), `password` **or** `privateKey` (+ `passphrase`), `remotePath`
  (default `.`), `pattern` (glob, default `*`), `modifiedWithinDays`, `take` (most-recent N).
- **azuretable**: `tableName` (required), `connectionString` **or** `accountUrl` + `sasToken`,
  `filter` (OData, templated with iteration variables), `select` (comma-separated columns).

### source.auth

Secret material is always a `${env:...}` or `${keyvault:vault/secret}` reference resolved at run
time, never an inline value.

| Key | Type | Description |
|---|---|---|
| `type` | enum | `none`, `api_key_header`, `api_key_query`, `bearer`, `basic`, `oauth2_client_credentials`, `oauth2_refresh_token`, `token_exchange`. |
| `secretRef` | secret ref | The token (bearer / api-key) or the password (basic). |
| `secondarySecretRef` | secret ref | The username for basic auth. |
| `headerName` | string | Header carrying the api key (`api_key_header`). |
| `paramName` | string | Query parameter carrying the api key (`api_key_query`). |
| `valuePrefix` | string | Prefix prepended to the header value. |
| `token` | object | How to obtain and apply an access token (the three token-based types). |

#### source.auth.token

| Key | Type | Description |
|---|---|---|
| `url` | string | The token endpoint. Required unless `discoveryUrl` supplies it. |
| `discoveryUrl` | string | An OIDC `.well-known/openid-configuration` URL whose `token_endpoint` supplies the URL. Takes precedence over `url`. |
| `method` | string | HTTP method for the token request. |
| `bodyKind` | enum | `form` (urlencoded, the OAuth2 default) or `json`. |
| `body` | map | Token-request body fields (`grant_type`, `client_id`, `client_secret`, `scope`, `audience`, `refreshToken`, ...). Values may be secret references and `{param}` templates. |
| `rawBody` | string | A pre-formed body sent verbatim, e.g. a Key Vault secret already holding the full urlencoded form. When set, `body` is ignored. |
| `headers` | map | Extra headers on the token request. |
| `basicAuthClient` | bool | Send `client_id`/`client_secret` as an HTTP Basic header instead of in the body; both are removed from `body`. |
| `tokenPath` | JSON path | Where the access token lives in the token response (`access_token`, `idToken`, ...). |
| `applyHeaderName` | string | The data-request header the token is applied to. |
| `applyPrefix` | string | Prefix on the applied token header value. |
| `refreshPerIteration` | bool | Re-acquire the token per fan-out iteration, for issuers that scope tokens per window. |

### source.request

Path, query values, headers, and body all support `{token}` substitution from the run's template
context: declared params, iteration variables, the incremental watermark's `bindVariable`, and bare
date tokens (`{yyyy}`, `{yyyyMMdd}`, `{window.from:yyyy-MM-dd}`). A referenced variable that is not
bound fails the run naming the token, never a silent blank. Escape a literal brace as `{{` or `}}`.

| Key | Type | Description |
|---|---|---|
| `method` | string | The HTTP method. |
| `path` | template | Path (and optional inline `?query`) appended to `baseUrl`. |
| `headers` | map | Request headers; values templated. |
| `query` | map | Query parameters sent on every page. Pagination parameters merge on top; auth query params last. Keys sort deterministically. |
| `bodyKind` | enum | `none`, `json`, `form`, `graphql` (wrapped as `{"query": ...}`), `soap` (sent as `text/xml`), `raw`. |
| `body` | template | The JSON object text, GraphQL query, SOAP envelope, or raw payload. |
| `bodyFields` | map | Form fields for a urlencoded body. |
| `contentType` | string | Explicit Content-Type for `soap`/`raw`. |
| `responseCharset` | string | Force the decoding of a response whose declared charset is wrong or missing. |

### source.pagination

Each fetched page lands as its own raw file.

| Key | Type | Description |
|---|---|---|
| `strategy` | enum | `none`, `page`, `offset`, `cursor_body`, `link_header`, `keyset`. |
| `maxPages` | int | Runaway guard: maximum pages per request pipeline, per iteration. |
| `pageParam` / `startPage` | string / int | `page`: the page-number parameter and its first value. |
| `pageVariable` | string | Binds the current page number as a template variable. |
| `offsetParam` / `limitParam` / `limit` | string / string / int | `offset`: the offset parameter, the page-size parameter, and the page size the offset advances by. |
| `cursorParam` / `cursorPath` | string / JSON path | `cursor_body`: the parameter the next cursor is sent as, and where it is read from. Pagination ends when the path yields nothing. |
| `keysetParam` | string | `keyset`: the parameter the resume id is sent as. |
| `keysetIdPath` | JSON path | The record field whose max across a page becomes the next resume id. |
| `keysetIdHeader` | string | Read the next id from a **response header** instead, for non-JSON bodies. |
| `stopOnStatus` | int | A non-2xx status that terminates pagination cleanly. The sentinel page is not landed. |
| `recordsPath` | JSON path | The record array used for empty-page detection, `skipEmpty`, keyset max-id scanning, and the response watermark. |

`page` and `offset` stop on an empty page; `link_header` follows RFC 5988 `Link rel="next"` until
absent; `keyset` stops on an empty page, an unchanged id, or `stopOnStatus`. Every strategy is
additionally capped by `maxPages`.

### source.iterate

Iterations apply outer-to-inner: the cartesian product produces one request pipeline per
combination, each of which may itself paginate.

| Key | Type | Description |
|---|---|---|
| `kind` | enum | `date_window`, `list`, or `ids_from`. Required. |
| `variable` | string | The template variable this iteration binds. |
| `granularity` | enum | `date_window` step size: `hourly`, `daily`, `monthly`. |
| `from` / `to` | time expression | Inclusive start / exclusive end: `now`, `today`, `yesterday`, `startOfMonth`, an offset (`now-3d`, `startOfMonth-1mo`; units `y\|mo\|w\|d\|h\|mi`), or an ISO date. A per-run backfill window replaces both. |
| `fromVariable` / `toVariable` | string | Variables bound to each step's bounds. `fromVariable` also becomes the reference date for bare date tokens within the step. |
| `overlapMinutes` | int | Re-read a tail of the previous window, for feeds that commit records late. |
| `values` | list | `list`: the static values to iterate. |
| `idRequest` | request | `ids_from`: the discovery request producing the id list. Runs once per outer combination, with the same auth. |
| `idPath` | JSON path | Selects the ids from the discovery response. |
| `idBindings` | map | Binds several fields per discovered id (variable name to response field), not just the id. |
| `batchSize` / `batchSeparator` | int / string | Group ids into joined batches, binding the variable to the batch. |

### source.reliability

| Key | Type | Default | Description |
|---|---|---|---|
| `timeoutSeconds` | int | | Per-request timeout; a timeout counts as a transient transport failure. |
| `rateLimitRps` | double | | Outbound token bucket, applied to every request including token and discovery calls. |
| `concurrency` | int | | Bounds how many requests are in flight at once. |
| `maxResponseBytes` | long | | Hard cap on one response held in memory; exceeding it fails the run naming the limit. |
| `verifyTls` | bool | `true` | TLS certificate verification. Disable only for a broken chain you explicitly trust. |
| `urlAllowlist` | list | | SSRF host allowlist for data requests: `*.suffix` matches the domain and any subdomain, anything else is an exact host. |
| `skipStatusCodes` | list\<int\> | | Non-retryable statuses that mark one request a tolerated SKIP instead of failing the run. |
| `retry.maxAttempts` | int | `4` | Total attempts per request; each page retries independently. |
| `retry.baseDelayMs` | int | `500` | First backoff delay; doubles per attempt. |
| `retry.maxDelayMs` | int | `30000` | Backoff ceiling, also bounding an honored `Retry-After`. |
| `retry.honorRetryAfter` | bool | `true` | Honor `Retry-After` as a **floor** raised to the computed backoff, never below it. |

Retries fire on transport failures and the transient statuses 408, 425, 429, 500, 502, 503, 504; any
other status is permanent.

**Private, loopback, link-local (including `169.254.169.254`), multicast, and unique-local addresses
are always blocked regardless of `urlAllowlist`.** Token and discovery calls keep that IP guard but
skip the host allowlist, since they are trusted configuration rather than data endpoints.

`skipStatusCodes` accepts only non-2xx codes in 100-599 (a 2xx is landed, never skipped), and the
retry policy still runs first, so a transient 429 retries before it can be skipped. Each skip is
logged as `acquire.skip` with the endpoint's error body and counted in `skippedRequests`.

### landing

The payload is landed **verbatim**: raw bytes, original format.

| Key | Type | Required | Description |
|---|---|---|---|
| `target` | template | yes | The raw-zone base: an ADLS Gen2 / Blob URI written through the shared Azure credential with no per-flow storage secret, or a local/UNC path. |
| `pathTemplate` | template | yes | Relative path per payload, minus extension. Binds date tokens, iteration variables, params, `{page}`, and `{runId}`. Two payloads rendering the same path in one run are disambiguated by page discriminator. |
| `format` | string | no | `auto` derives the extension from the response Content-Type (unknown becomes `bin`), or set it explicitly. |
| `compression` | enum | no | Optional gzip; `.gz` is appended after the format extension. |
| `overwrite` | bool | no | When false, a colliding write fails rather than clobbers (ETag-guarded on Azure). |
| `persistHeaders` | bool | no | Write a `<file>.headers.json` sidecar per page, with sensitive headers redacted. |
| `skipEmpty` | bool | no | Skip landing a payload whose record array is empty. Counted separately in the run result. |
| `skipUnchanged` | bool | no | Skip rewriting a file whose target already holds byte-identical content, so an unchanged re-fetch does not bump the modified time and re-trigger downstream ingestion. |
| `protect` | list | no | Data-protection rules; see below. |

#### Data protection at the landing boundary

`landing.protect` rules are applied to the payload **before** it is written, in declaration order.
It is fail-closed: `json`, `jsonl`, `xml`, and `csv` are protectable, and any other format, or a
payload that fails to parse, fails the run rather than landing unprotected.

Each rule takes a `path` (a JSON path, an XML element path, or a CSV column) and an `action`:
`remove`, `redact`, `mask`, `hash`, `hmac`, `tokenize`, `encrypt`, or `generalize`. The keyed
transforms take a `secret` (required for `hmac` and `encrypt`) and a `scope` controlling
linkability: `transaction` (run-scoped salt), `relationship` (salted by flow name), or `person` (no
extra salt, so consistent across sources). See
[concept-landing-data-protection](../concepts/landing-data-protection.md) for the full transform and
parameter surface.

### incremental

Single-endpoint form only. The watermark advances only on a successful run, so a failed run never
skips the data it failed on. A full-load run (`--full`) or a backfill window ignores the stored mark.

| Key | Type | Description |
|---|---|---|
| `source` | enum | `response` (track the max of a response field, persisted in the run record), `lake` (derive from what is landed), or `sql` (read from loaded data with a query the flow supplies). |
| `column` | string | `response`: the record field whose lexicographic max is the watermark, read across `pagination.recordsPath`. |
| `bindVariable` | string | The template variable the resolved watermark is bound to at run start. Keyset pagination reads the watermark directly and needs no binding. |
| `seed` | string | The watermark used on the first run, or when the query returns no row. |
| `connection` | string | `sql`: the connection the probe reads from. |
| `query` | string | `sql`: the query supplying the resume point, taken verbatim. One column is a single watermark for the flow; **two** columns is one watermark per entity (key, then resume point). |
| `keyVariable` | string | `sql`: the fan-out variable a per-entity watermark is keyed by. An entity with no row falls back to `seed`. |

A `response` watermark lives in the flow's run history under `.sqlflow/runs`, which is node-local and
does not survive a redeploy or a change to the flow's identity. `sql` exists for feeds where that
fragility matters: the loaded table survives both.

## Run summary

The run result distinguishes files written from files unchanged, because a rolling-window feed
re-fetches the same days every run and lands them byte-identical. A summary quoting only the landed
count makes a run that wrote nothing read as new data arriving, and the downstream flows that
correctly loaded no rows then look broken by comparison. `pagesFetched`, `iterations`, and
`skippedRequests` complete the picture.

## See also

- [sftp](sftp.md) for a pure file transfer with no request semantics, and [cpy](cpy.md) for
  object-store to object-store copies.
- [source](source.md) and the source-type pages for the file flows that ingest what this lands.
- The per-attribute key census in `keys.api.json`, which the LSP and VSCode extension consume.
