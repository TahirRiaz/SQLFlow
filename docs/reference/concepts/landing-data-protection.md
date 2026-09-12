---
id: concept-landing-data-protection
title: Landing-time data protection (landing.protect)
type: concept
summary: landing.protect rules scrub, pseudonymise, or generalise PII in the raw payload before it is written to the lake, format-aware across json, jsonl, xml, and csv.
keywords:
  - protect
  - pii
  - gdpr
  - redact
  - mask
  - hash
  - hmac
  - tokenize
  - encrypt
  - pseudonymisation
  - generalize
  - data protection
  - scrub
  - landing
related:
  - concept-file-discovery-and-lifecycle
  - concept-connections-and-secrets
  - flow-source
sourceRefs:
  - src/SqlFlow.Core/Acquire/AcquireProtect.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
  - src/SqlFlow.Acquire/Runtime/Protection/PayloadProtector.cs
  - src/SqlFlow.Acquire/Runtime/Protection/PayloadTransforms.cs
  - src/SqlFlow.Acquire/Runtime/Protection/XmlProtection.cs
  - src/SqlFlow.Acquire/Runtime/Protection/CsvProtection.cs
  - src/SqlFlow.Acquire/Engine/LandingPipeline.cs
  - src/SqlFlow.Yaml/YamlAcquireFlowLoader.cs
---

# Landing-time data protection (`landing.protect`)

An acquisition (`flowType: api`) normally lands the upstream payload VERBATIM. That is the right default for
fidelity, but it is the wrong default when the upstream response carries personal data the estate must not
persist: by the time a downstream view could scrub it, the raw file already holds it. `landing.protect` is the
one sanctioned exception to land-verbatim: a list of rules applied to the payload INSIDE the landing sink,
before a single byte reaches the lake, so protected fields never exist at rest.

The step is fail-closed. A payload that cannot be parsed in its landed format, a rule naming a CSV column the
header lacks, or rules on a format with no structural model (`bin`, `txt`, spreadsheets) FAIL the run rather
than landing the payload unprotected. Requested protection is never silently skipped.

## Where rules live

Each landing (top-level or per item) carries its own `protect:` list; rules apply in declaration order.

```yaml
landing:
  target: abfss://datalakev2@dwdatalakeprod.dfs.core.windows.net/raw/hentmeg/api/requests
  pathTemplate: "history/{window.from:yyyy}/hentmeg_{window.from:yyyyMMdd}"
  format: json
  protect:
    - { path: "$.data[*].rider",                   action: remove }
    - { path: "$.data[*].scheduledPickupAddress",  action: remove }
    - { path: "$.data[*].scheduledDropoffAddress", action: remove }
    - { path: "$.data[*].phone",   action: redact, mode: phone }
    - { path: "$.data[*].riderId", action: hmac, secret: "${keyvault:sqlflow-v3-secrets/hentmeg-pii-key}", outputLength: 16 }
```

## Path semantics per format

The rule `path` is interpreted per the landed format (the same extension the file lands with):

- **json / jsonl**: a JSON path with an optional leading `$`, dotted properties, `[*]` mapping every array
  element, and `[n]` indices (`$.data[*].rider`). jsonl applies the rules line by line.
- **xml**: an element path starting AT the document root (`requests.request.rider`); a repeated element name
  matches every occurrence, and a trailing `@name` addresses an attribute (`riders.rider.@name`).
- **csv**: the header column name (quote-aware RFC-4180 parsing; the first line must be a header; untouched
  fields keep their raw bytes; a rule's `delimiter` param overrides the auto-detection among `; , tab |`).

A JSON/XML path matching nothing is a no-op (payload shapes vary per record); a missing CSV column is an error
because the file would otherwise land with the named protection not applied.

## Actions

| action | effect | key material |
| --- | --- | --- |
| `remove` | Delete the field (CSV: blank the value, keeping the schema) | none |
| `redact` | Replace with a constant (`replacement`, default `[REDACTED]`) or a suppression pattern: `mode: partial` (`keepFirst`/`keepLast`/`maskChar`), `email`, `phone`, `card` | none |
| `mask` | Keep the first `show` (and last `showLast`) characters, mask the rest | none |
| `hash` | Unkeyed SHA-256 hex fingerprint; deterministic everywhere, but brute-forceable for low-entropy values | none |
| `hmac` | Keyed one-way pseudonym: HMAC-SHA256 (default) or `algorithm: pbkdf2` (`iterations`); `outputLength` truncates | `secret` required |
| `tokenize` | Token substitution (`format`, default `tok_{short}`): keyed = deterministic across runs (joins survive, not reversible), unkeyed = random per run, consistent within the payload | `secret` optional |
| `encrypt` | Reversible deterministic authenticated encryption (AES-256-GCM with a plaintext-derived synthetic nonce, the SIV construction); the key holder can decrypt | `secret` required |
| `generalize` | Precision reduction: `mode: year/month/quarter/decade` (dates), `age_range` (`bucket`), `zip3`/`zip2`, `round` (`step`, stays numeric JSON) | none |

## Linkability scope (keyed transforms)

`scope` controls how consistently the same input maps to the same output by scoping the key-derivation salt:

- `transaction`: a run-scoped random salt; the same value maps differently across runs (no linkability).
- `relationship` (default): salted by the flow name plus an optional `relationship` param; consistent within
  this source, unlinkable to other sources using the same key.
- `person`: no extra salt; globally consistent wherever the same key is used (full cross-source linkability).

Key material is always a `${...}` secret reference resolved once per run; the plaintext key never appears in
YAML or logs, and per-purpose keys are derived from it with HKDF-SHA256 domain separation.

## Ordering and guarantees

Protection runs before compression and before the byte-identical skip-unchanged comparison, through the single
landing sink every transport (HTTP, SFTP, Azure Table) writes through, so every landed file of a protect-carrying
item is protected regardless of transport. Note that `transaction`-scope and unkeyed-tokenize outputs change per
run, which defeats skip-unchanged's re-land suppression for those files; keyed deterministic transforms preserve
it.

## When to use it

The motivating case is a source whose legacy producer scrubbed PII before writing (Hentmeg's runbook blanked
rider names and addresses per GDPR): re-establishing the acquisition against the live API MUST NOT reintroduce
PII at rest, and mirroring the producer's scrubbed output from the old storage account is not an acquisition
(the old lake is a target, not a source). `landing.protect` lets the V3 flow own both the fetch and the
protection declaratively.
