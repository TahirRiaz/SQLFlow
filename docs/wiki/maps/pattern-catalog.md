---
id: wiki-pattern-catalog
title: "Pattern catalog: which data-engineering problem maps to which SQLFlow shape"
type: map
summary: "Problem-first index over 751 production pipelines, mapping each recurring data-engineering problem to the SQLFlow shape that solves it."
keywords:
  - patterns
  - catalog
  - problem
  - recipes
  - production
  - estate
  - index
rawRefs:
  - docs/reference/flow/keys.api.json
related:
  - wiki-api-authentication
  - wiki-api-pagination
  - wiki-api-fanout
  - wiki-api-resume-and-watermarks
  - wiki-api-resilience
  - wiki-landing-and-protection
  - wiki-file-ingestion-shaping
  - wiki-relational-incremental
  - wiki-upsert-and-history
  - wiki-orchestration-and-scheduling
updated: 2026-09-09
---

# Pattern catalog

Start here when the question is **"how do I solve X with SQLFlow"** rather than "what does key Y do".
Key semantics live in [docs/reference/](../../reference/); this catalog maps problems onto shapes and
points at the production flow that already does it.

## Where the patterns came from

Harvested structurally from the production pipeline repository (`dwh-pipelines-prod`): 751 flow
documents across 39 source folders, 669 distinct key paths, parsed rather than sampled. Frequencies
quoted on the pattern pages are counts from that harvest, so "10 folders" means ten independent
sources reached for the same construct.

The estate's shape, for calibration:

| Flow kind | Documents | What it is |
| --- | --- | --- |
| `ing` | 512 | relational source to target |
| file (no `flowType`) | 138 | csv/json/xml/xls file to table |
| `cpy` | 31 | file copy between stores |
| `api` | 18 | acquisition from a third-party system |
| `sp`, `scm` | 4 each | stored procedure, schema operation |
| `sftp` | 3 | vendor drop |
| `cal`, `inv` | 1 each | calendar generation, external pipeline trigger |

## Acquisition

| Problem | Shape | Page |
| --- | --- | --- |
| The vendor's login is unlike the last one's | `source.auth` with a secret **reference**, never a value | [api-authentication](../patterns/api-authentication.md) |
| The client id must go in a Basic header | `token.basicAuthClient` | [api-authentication](../patterns/api-authentication.md) |
| The whole token body is the secret | `token.rawBody` from Key Vault | [api-authentication](../patterns/api-authentication.md) |
| There is no token URL, only OIDC discovery | `token.discoveryUrl` | [api-authentication](../patterns/api-authentication.md) |
| The token is scoped to the window fetched | `token.refreshPerIteration` | [api-authentication](../patterns/api-authentication.md) |
| The endpoint returns a slice at a time | `pagination.strategy` + `recordsPath` | [api-pagination](../patterns/api-pagination.md) |
| The cursor repeats forever on the last page | `keyset`, which derives position from the data | [api-pagination](../patterns/api-pagination.md) |
| The body is binary so the id is in a header | `keysetIdHeader` | [api-pagination](../patterns/api-pagination.md) |
| The feed signals "done" with a non-2xx | `stopOnStatus` | [api-pagination](../patterns/api-pagination.md) |
| History needs a day-by-day walk | `iterate.date_window` | [api-fanout](../patterns/api-fanout.md) |
| A day-sized window silently truncates | `granularity: hourly` | [api-fanout](../patterns/api-fanout.md) |
| The source corrects history in place | `granularity: monthly` re-read | [api-fanout](../patterns/api-fanout.md) |
| One endpoint per entity, roster unknown | `iterate.ids_from` + `idPath` | [api-fanout](../patterns/api-fanout.md) |
| The per-entity call needs more than an id | `idBindings` | [api-fanout](../patterns/api-fanout.md) |
| 2,000 ids, one at a time, rate-limited | `batchSize` + `batchSeparator` | [api-fanout](../patterns/api-fanout.md) |
| Resume where the last run stopped | `incremental` | [api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md) |
| The watermark must survive a redeploy | `incremental.source: sql` | [api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md) |
| Each entity has its own position | `incremental.keyVariable` | [api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md) |
| One dead id kills the whole sweep | `skipStatusCodes` | [api-resilience](../patterns/api-resilience.md) |
| The vendor throttles us | `rateLimitRps`, `concurrency` | [api-resilience](../patterns/api-resilience.md) |
| A URL could reach cloud metadata | `urlAllowlist` + the always-on IP guard | [api-resilience](../patterns/api-resilience.md) |
| Norwegian text arrives mangled | `request.responseCharset` | [api-resilience](../patterns/api-resilience.md) |
| One flow, several endpoints | the multi-item form (`items[]`) | [api-fanout](../patterns/api-fanout.md) |

## Landing and files

| Problem | Shape | Page |
| --- | --- | --- |
| A vendor change must not break the fetch | land verbatim; parse downstream | [landing-and-protection](../patterns/landing-and-protection.md) |
| Re-landing identical files re-triggers everything | `skipUnchanged` | [landing-and-protection](../patterns/landing-and-protection.md) |
| PII must never reach the lake | `landing.protect`, fail-closed | [landing-and-protection](../patterns/landing-and-protection.md) |
| Pseudonyms must (not) join across sources | `protect.scope` | [landing-and-protection](../patterns/landing-and-protection.md) |
| A copy rewrote every modified time | `fileDate.from` / `pattern` | [file-ingestion-shaping](../patterns/file-ingestion-shaping.md) |
| Nested arrays, wrong grain | `explodePaths` | [file-ingestion-shaping](../patterns/file-ingestion-shaping.md) |
| 400 columns nobody reads | `includePaths` (and its silent-empty trap) | [file-ingestion-shaping](../patterns/file-ingestion-shaping.md) |
| Inference keeps mistyping a column | `schema.overrides` | [file-ingestion-shaping](../patterns/file-ingestion-shaping.md) |
| A ported `IS NOT NULL` now drops rows | empty lands as NULL, not `''` | [string-first-landing](../decisions/string-first-landing.md) |

## Relational ingestion

| Problem | Shape | Page |
| --- | --- | --- |
| 2B rows, a few million new daily | `incremental.dateColumn` | [relational-incremental](../patterns/relational-incremental.md) |
| Rows commit after their timestamp | `overlapDays` | [relational-incremental](../patterns/relational-incremental.md) |
| The first load will never finish | `initLoad` chunking, `load.threads` | [relational-incremental](../patterns/relational-incremental.md) |
| A filtered slice should append, not reconcile | `filterIsAppend` | [relational-incremental](../patterns/relational-incremental.md) |
| Rows with a NULL key multiply every run | the INSERT is an anti-join | [upsert-and-history](../patterns/upsert-and-history.md) |
| Row count tracks files, not facts | provenance columns in the merge key | [upsert-and-history](../patterns/upsert-and-history.md) |
| Keep full history of every change | `versioning.temporal` | [upsert-and-history](../patterns/upsert-and-history.md) |
| The rebuilt table breaks a `SELECT *` | a compatibility view under the old name | [upsert-and-history](../patterns/upsert-and-history.md) |
| A frozen era and a live feed share a name | `_hist` + `_live` behind one view | [upsert-and-history](../patterns/upsert-and-history.md) |

## Orchestration

| Problem | Shape | Page |
| --- | --- | --- |
| A source's flows must run in order | one schedule on the anchor, lineage waves | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |
| Stages must not run concurrently | `schedule.after` chaining | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |
| Retire a source without losing its history | `mode: disabled` | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |
| A step lives in another system | `flowType: inv` + service principal | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |
| What breaks if this column changes? | the `subscribers` registry | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |
| Lineage has a hole where data enters | `output` on a copy/SFTP flow | [orchestration-and-scheduling](../patterns/orchestration-and-scheduling.md) |

## Traps worth reading before authoring

| Trap | Page |
| --- | --- |
| A misspelled or misplaced key is silently ignored | [ignored-yaml-keys](../incidents/ignored-yaml-keys.md) |
| The key census is behind the engine on nine paths | [census-drift](census-drift.md) |
| A design document under `docs/` may be historical | [design-doc-drift](design-doc-drift.md) |
| `ids_from` cannot see retired entities | [api-fanout](../patterns/api-fanout.md) |
| A node-local watermark dies on redeploy | [api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md) |
