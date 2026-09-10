---
id: wiki-landing-and-protection
title: "Pattern: landing raw payloads verbatim, and the one time you must not"
type: pattern
summary: Why acquisition lands bytes unchanged, how the path template partitions the lake, and why PII scrubbing is the single deliberate exception to landing verbatim.
keywords:
  - landing
  - pathTemplate
  - raw zone
  - skipUnchanged
  - skipEmpty
  - protect
  - pii
  - pseudonymisation
  - gdpr
sourceRefs:
  - src/SqlFlow.Acquire/Engine/LandingPipeline.cs
  - src/SqlFlow.Acquire/Landing/AzureRawLandingStore.cs
  - src/SqlFlow.Acquire/Runtime/Protection/PayloadProtector.cs
  - src/SqlFlow.Acquire/Runtime/Protection/ProtectPath.cs
referenceRefs:
  - concept-landing-data-protection
  - concept-file-discovery-and-lifecycle
related:
  - wiki-string-first-landing
  - wiki-file-ingestion-shaping
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: landing raw payloads verbatim, and the one time you must not

**The problem.** Acquisition and interpretation are different jobs with different failure modes. If
the fetch also parses, a vendor changing a field breaks the fetch, and the bytes that would have
proved what happened are never written down.

**The rule.** `landing` writes the payload **verbatim**: original bytes, original format, no CSV
conversion, no flattening. Downstream `json`/`csv`/`xml` file flows ingest the landed files. This is
the acquisition-side counterpart of [string-first landing](../decisions/string-first-landing.md).

There is exactly one deliberate exception, `landing.protect`, covered below.

## The path template is the lake's partitioning

```yaml
landing:
  target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/trips
  pathTemplate: "history/{yyyy}/{MM}/vendor_trips_{window.from:yyyyMMdd}_{page}"
  format: auto
```

`target` is the base location, written through the shared Azure credential with no per-flow storage
secret. `pathTemplate` is the relative path per payload, minus extension, and it binds date tokens,
iteration variables, declared params, `{page}`, and `{runId}`.

Getting this right matters more than it looks. The path is what a downstream file flow globs, what
makes a re-run idempotent, and what carries the provenance a lake file otherwise lacks. When two
payloads render the same path in one run, the later is disambiguated with its page discriminator, so
pages never silently overwrite each other.

**Date the file from the window it covers, not from when it was fetched.** An object store's
modified time is rewritten by any copy, so it is not provenance. Encoding the covered date in the
name, and reading it back with the ingestion-side `source.options.fileDate.from`/`pattern`, keeps a
file's meaning attached to the file.

`format: auto` derives the extension from the response `Content-Type` (`+json`/`+xml` suffixes
honoured, unknown becomes `bin`), or set it explicitly.

## Not re-landing what did not change

`skipUnchanged` compares a content hash against what the target already holds and skips the write
when identical. This is not a storage optimisation, it is a downstream-triggering one: rewriting a
byte-identical file bumps its modified time and re-triggers every ingestion flow watching that path.

A rolling-window feed re-fetches the same days every run, so without this the estate re-loads the
same data daily. The run summary distinguishes new files from unchanged ones for exactly this
reason: quoting the landed count alone makes a run that wrote nothing read as new data arriving.

`skipEmpty` avoids writing a zero-row file when the record array (located by
`pagination.recordsPath`) is empty. `overwrite: false` turns a collision into a failure rather than a
clobber, ETag-guarded on Azure.

## The exception: scrubbing PII at the boundary

Some payloads must never be persisted raw. `landing.protect` applies rules to the payload **before**
it is written, in declaration order, so sensitive fields never reach the lake at all rather than
being cleaned up later.

```yaml
landing:
  protect:
    - path: $.trips[*].riderEmail
      action: hmac
      secret: ${keyvault:my-vault/pseudonym-key}
      scope: person
    - path: $.trips[*].pickupAddress
      action: remove
```

It is **fail-closed**: json, jsonl, xml, and csv are protectable, and any other landed format, or a
payload that fails to parse, fails the run rather than landing unprotected. That is the correct
trade. A protection rule that silently no-ops is worse than a broken pipeline.

The actions span the usual spectrum: `remove`, `redact`, `mask`, `hash` (unkeyed SHA-256), `hmac`
(keyed one-way pseudonym), `tokenize`, `encrypt`, and `generalize` (year/month/quarter/decade/
age_range/zip3/round).

**`scope` is the design decision, not the algorithm.** It controls linkability of the keyed
transforms:

| Scope | Salt | Consequence |
| --- | --- | --- |
| `transaction` | per run | the same person maps differently every run; no longitudinal analysis |
| `relationship` | flow name (+ optional label) | consistent within this source only |
| `person` | none extra | globally consistent, so joinable across sources |

Choosing `person` buys cross-source joins and costs re-identification resistance. Choosing
`transaction` is the reverse. That is a governance decision to make deliberately, and it is worth
recording why in the flow.

## Production exemplars

| Flow folder | Pattern |
| --- | --- |
| `hentmeg/` | `protect` scrubbing rider PII at the acquisition boundary |
| `questback/` | `protect` on survey responses; per-entity landing paths |
| `billettapp/` | `skipEmpty` on a feed with frequent empty windows |
| 8 folders | `skipUnchanged` on rolling-window feeds |
