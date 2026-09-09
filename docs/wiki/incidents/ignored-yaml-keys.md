---
id: wiki-ignored-yaml-keys
title: "A misspelled or misplaced YAML key does nothing, and says nothing"
type: incident
summary: "Loaders ignore unmatched properties, so a wrong or misplaced key is dropped in silence; a live production flow sets a retry knob that does not exist."
keywords:
  - ignoreUnmatchedProperties
  - silent
  - yaml
  - validation
  - backoffSeconds
  - truncateBeforeLoad
  - misconfiguration
sourceRefs:
  - src/SqlFlow.Yaml/YamlAcquireFlowLoader.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
referenceRefs:
  - flow-overview
  - guide-canonical-authoring
related:
  - wiki-api-resilience
  - wiki-upsert-and-history
  - wiki-census-drift
updated: 2026-09-09
---

# A misspelled or misplaced YAML key does nothing, and says nothing

**The mechanism.** Every flow loader builds its deserializer with `IgnoreUnmatchedProperties`. A key
that does not bind to a property is dropped without a warning. There is no error, no log line, and
no difference in the run summary between a flow that applied your setting and one that never saw it.

That is a reasonable choice for forward compatibility, and it has a sharp edge: **YAML that looks
configured and is not.**

## The live case

`questback/questback_00_api.yaml` declares:

```yaml
reliability:
  retry:
    maxAttempts: 4
    backoffSeconds: 5
```

`AcquireRetry` has four properties: `MaxAttempts`, `BaseDelayMs`, `MaxDelayMs`, `HonorRetryAfter`.
There is no `backoffSeconds` anywhere in the source tree. The flow runs with `maxAttempts: 4` and the
**default** backoff, and has done since it shipped. Nothing is broken, which is precisely why nobody
noticed: the retry works, just not with the delay someone intended to set.

## The other shape: right key, wrong section

The same trap catches a key that exists but is nested one level off. `truncateBeforeLoad` belongs
under `target:`. Placed under `load:`, it binds to nothing and the table is never truncated, so an
append-mode load that was meant to replace its contents instead accumulates a copy per run. The
symptom is a row count that is an exact multiple of what it should be.

Both shapes fail the same way: silently, and in the direction of "the default happened".

## How to catch it

**Diff against the key census, not against memory.** `docs/reference/flow/keys*.json` enumerates
every bindable key path per flow kind. A key not in the census for that flow kind is either invalid
or a census gap, and both are worth resolving before shipping. Note that the census can itself be
behind the code; see [census-drift](../maps/census-drift.md).

**Treat "I set it and nothing changed" as evidence.** The first hypothesis should be that the key
never bound, not that the setting had no effect.

**Verify a new knob once, in the run it first matters.** A retry delay is observable in the timing
between attempts in the run log. A truncate is observable in the row count. One deliberate check at
authoring time costs less than discovering it years later.

## Why this is not simply fixed

Making unmatched keys fatal would reject every flow written against a newer schema than the engine
running it, which is a real constraint in an estate where the pipeline repository and the deployed
control plane are separately versioned. The productive direction is authoring-time validation against
the census rather than load-time rejection, which is where the canonical-authoring lints sit.

Neither of the two cases above is repaired by this page. They are recorded here so the next person
who finds a setting that does not take has a name for it.
