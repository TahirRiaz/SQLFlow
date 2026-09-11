# Generic acquisition (flowType: api)

One declarative engine that replaces the estate's Azure Automation runbooks. An `api` flow fetches from a
third-party system (an HTTP API, an SFTP server, or an Azure Storage Table) and lands the **raw**
response payloads as files in the data lake, one file per page/iteration, preserving the
original format byte-for-byte. It performs no CSV/flatten transform: the existing SQLFlow `json`/`csv`/`xml`
file-flows ingest the landed files into SQL. Integrations are now version-controlled YAML managed centrally
alongside every other pipeline, testable and schedulable through the same control plane.

## Why a two-stage split

The old runbooks fetched *and* reshaped to CSV *and* uploaded, in one opaque script. This engine does only
acquisition and lands raw. That keeps one code path (ingestion stays the mature file-flow path), makes a fetch
replayable, and means a schema change downstream never requires touching the integration.

    [api flow] --raw JSON/XML/bin--> raw zone --existing json/csv/xml flow--> SQL

## One flow, many endpoints (`items:`)

A source system is rarely one endpoint. Cyclehire, for example, was **11 separate runbooks** all hitting
`api.cyclehire.example` under one token: bikes, alerts, inventory, issue reports, repair orders, and so on.
Rather than 11 flow files, one `api` flow declares a shared connection envelope under `source` (transport, base
URL, auth, reliability) and an **`items:`** list, one entry per endpoint, each with its own `request`,
`pagination`, `iterate`, and `landing`. This mirrors how a `cpy` copy flow declares multiple `items:`.

```yaml
flowType: api
name: cyclehire_00_api
batch: Cyclehire
source:                       # shared envelope: applied to every item
  baseUrl: https://api.cyclehire.example
  auth:
    type: token_exchange
    token: { url: https://api.cyclehire.example/api/token, rawBody: "${keyvault:sqlflow-v3-secrets/cyclehire-token}", tokenPath: access_token }
  reliability: { rateLimitRps: 8, urlAllowlist: ["*.cyclehire.example"] }
items:
  - name: bikes
    request: { path: /api/Bikes }
    landing: { target: abfss://datalakev2@dwdatalakeprod.dfs.core.windows.net/raw/cyclehire/api/bikes, pathTemplate: "history/{yyyy}/{MM}/cyclehire_bikes_{yyyyMMdd}", format: json }
  - name: inventory
    request: { path: /api/Inventory }
    landing: { target: abfss://datalakev2@dwdatalakeprod.dfs.core.windows.net/raw/cyclehire/api/inventory, pathTemplate: "history/{yyyy}/cyclehire_inventory_{yyyyMMdd}", format: json }
```

The engine resolves the HTTP client and the token **once per run**, then fetches every item in order,
accumulating one run result. Each item lands to its own raw path, so a downstream file/pre flow that reads that
path binds to it: **one acquisition pipeline feeds several downstream loads and lineage stays intact** (one
pipeline node, one landing node per item, one dependency per consumer).

Exactly one form is allowed. In the `items:` form, `source` holds only the shared envelope: putting a
`request`/`pagination`/`iterate` on `source`, or a top-level `landing`, alongside `items` is a validation error.
`incremental:` (a single per-run watermark) is single-endpoint only; a multi-item flow expresses incrementality
through each item's date-window `iterate`. A single-endpoint flow keeps the flat `source.request` + top-level
`landing` shape unchanged.

## Landing-time data protection

When the upstream response carries fields the estate must not persist (rider names, addresses, phone numbers),
declare `landing.protect` rules on the item: they scrub, pseudonymise, or generalise the matched fields INSIDE
the landing sink, before any byte reaches the lake, and fail the run rather than land unprotected. Format-aware
across json/jsonl/xml/csv; keyed transforms (hmac/tokenize/encrypt) take a `${...}` secret and a linkability
`scope`. See `docs/reference/concepts/landing-data-protection.md` for the full rule model.

    landing:
      target: abfss://.../raw/hentmeg/api/requests
      pathTemplate: "history/{window.from:yyyy}/hentmeg_{window.from:yyyyMMdd}"
      format: json
      protect:
        - { path: "$.data[*].rider",                   action: remove }
        - { path: "$.data[*].scheduledPickupAddress",  action: remove }
        - { path: "$.data[*].phone", action: redact, mode: phone }

## Run it

    sqlflow run samples/api/jsonplaceholder-basic.flow.yaml     # live, no auth, lands to ./_landing

Incremental resume, retries, rate limiting, run history, and scheduling all come from the shared engine.

## Runbook pattern -> YAML feature coverage

Every capability observed across the 40 runbooks maps to a schema feature. The runbooks were used only to derive
the patterns; the engine is verified against public APIs and a deterministic test suite.

| Runbook pattern (examples) | YAML feature |
| --- | --- |
| No auth (SVV, opencom, geonorge, stavanger) | `auth.type: none` |
| Static bearer token (shiplog, norled, ryde, hentmeg) | `auth.type: bearer` + `secretRef` |
| API key header, custom name (App Insights `X-Api-Key`) | `auth.type: api_key_header` + `headerName` |
| Token as query param (Skynet `?token=`) | `auth.type: api_key_query` + `paramName` |
| Non-standard bearer header (easypark `X-Authorization`) | `token.applyHeaderName` + `applyPrefix` |
| OAuth2 client-credentials, form body (Voi, fleetreg) | `auth.type: oauth2_client_credentials`, `token.bodyKind: form` |
| OAuth2 client-credentials, JSON body + audience (Entur) | `token.bodyKind: json`, `token.body` |
| OAuth2 Basic-auth client (Voi id:secret) | `token.basicAuthClient: true` |
| OAuth2 refresh-token exchange (easypark) | `auth.type: oauth2_refresh_token` |
| OIDC discovery for the token endpoint (fleetreg) | `token.discoveryUrl` |
| Custom token exchange, pre-formed form body in KV (Cyclehire) | `auth.type: token_exchange`, `token.rawBody` |
| Per-window token refresh (Voi) | `token.refreshPerIteration: true` |
| GET / POST | `request.method` |
| GraphQL POST (SVV) | `request.bodyKind: graphql` |
| SOAP-over-POST envelope (Questback) | `request.bodyKind: soap` |
| JSON / form-urlencoded body | `request.bodyKind: json` / `form` + `bodyFields` |
| Path + query templating with date/id tokens | `{placeholder}`, `{yyyyMMdd}`, `{window.from:fmt}` |
| Rolling boundary inside a request, with no fan-out (Questback's closed-quest window) | `{now-6mo:yyyy-MM-dd}`, `{startOfMonth:yyyy-MM-dd}` |
| Credentials inside the request body rather than a header (SOAP `<Password>`) | `${keyvault:...}` in `request.body` (resolved per item) |
| Page-number pagination (Questback, fleetreg) | `pagination.strategy: page` |
| Page number carried in the request BODY, not the query (SOAP `<PageNo>`) | `pagination.pageVariable` + the `{token}` in `request.body` |
| Offset/limit pagination | `pagination.strategy: offset` |
| Cursor-in-body pagination | `pagination.strategy: cursor_body` + `cursorPath` |
| Link-header pagination (GitHub-style, SVV NVDB next-href) | `pagination.strategy: link_header` |
| Keyset / `idAfter` resume, stop on sentinel status (Entur) | `pagination.strategy: keyset`, `stopOnStatus` |
| Date window day-by-day (easypark, fjord1, shiplog) | `iterate: date_window`, `granularity: day` |
| Hourly sub-window (Voi, Ryde 24x/day) | `granularity: hour` |
| Monthly window (svv_index) | `granularity: month` |
| Static list fan-out (operatorIds, routes, lines) | `iterate: list` + `values` |
| Ids from a prior call (bikes -> alerts/sessions) | `iterate: ids_from` + `idRequest` + `idPath` |
| Ids out of an XML/SOAP discovery response (Questback quests) | `ids_from` with an XPath `idPath` (namespaces stripped) |
| Fan-out needing more than the id per record (Questback `questId` + `securityLock`) | `ids_from` + `idBindings` (variable -> path, relative to each record) |
| Many endpoints of one system consolidated (Cyclehire's 11 runbooks) | `items:` list, one entry per endpoint over the shared `source` |
| Batched-id chunking, max N per request (svv 50 ids) | `ids_from` + `batchSize` + `batchSeparator` |
| Retry with backoff (shiplog, norled, questback) | `reliability.retry` |
| Honor Retry-After, retry 408/425/429/5xx | built in (`retry.honorRetryAfter`) |
| Rate limiting / request budget (svv batching) | `reliability.rateLimitRps` |
| Per-request timeout (60/120/300s) | `reliability.timeoutSeconds` |
| PII scrubbed before landing (hentmeg's GDPR blanking of rider/addresses) | `landing.protect` rules (remove/redact/mask/hash/hmac/tokenize/encrypt/generalize) |
| Empty-data skip (easypark, voi) | `landing.skipEmpty` |
| Unchanged-file skip (same fetch, no rewrite) | `landing.skipUnchanged` (default true) |
| History path with year/month/day + id tokens | `landing.pathTemplate` |
| Raw JSON passthrough (fjord1/norled json) | `landing.format: json` (or `auto`) |
| Binary/XLSX response (Entur report) | `landing.format: auto` (lands `.bin`/declared ext) |
| gzip on landing | `landing.compression: gzip` |
| Incremental resume from lake state (Entur lastReportId) | `incremental` + keyset |
| Resume from what was LOADED, not from a run record (survives redeploys) | `incremental.source: sql` + `connection` + `query` |
| Per-entity high-water marks, so one lagging entity does not refetch them all | two-column watermark `query` + `keyVariable` |
| SFTP download, modified-within window (Cyclehire settlement, Ferde) | `transport: sftp` |
| Azure Storage Table query (fjord1, apc) | `transport: azuretable` + OData `filter` |
| Secrets from Key Vault, never inline | `${keyvault:vault/secret}` refs |
| SSRF safety (block metadata/private IPs) | `reliability.urlAllowlist` + built-in IP guard |

### Residual runbooks (not API integrations)

Three runbooks are not third-party integrations and stay outside this engine: the two Azure Automation job-status
log exports (`Fail_jobs`, `Maintenance`) read the Azure control plane, and `boatbooking`/`Z_ebk_test_72` copy
lake-to-lake. The first two are platform observability; the copy is an ingestion concern, not an acquisition one.

## Debugging

The GUI's **Integrations** page lists every `api` flow with its YAML definition (View code) and provides a
DeltaForge-style debugger: edit the YAML, run a safe **Test** invoke (fetch without landing), and inspect the
request, the raw response, timing, the pagination steps, and where each page would land. Production runs go through
the normal run/schedule path like any other pipeline.
