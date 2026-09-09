---
id: wiki-api-resilience
title: "Pattern: surviving a third-party API (retries, rate limits, dead ids, SSRF)"
type: pattern
summary: "The reliability envelope every request runs under: retries, rate limits, tolerating dead ids, and the SSRF guard the allowlist cannot widen."
keywords:
  - retry
  - backoff
  - rate limit
  - concurrency
  - skipStatusCodes
  - urlAllowlist
  - ssrf
  - timeout
  - responseCharset
sourceRefs:
  - src/SqlFlow.Acquire/Runtime/RetryPolicy.cs
  - src/SqlFlow.Acquire/Runtime/RateLimiter.cs
  - src/SqlFlow.Acquire/Runtime/UrlGuard.cs
  - src/SqlFlow.Acquire/Runtime/HttpExecutor.cs
  - src/SqlFlow.Acquire/Runtime/HeaderRedaction.cs
related:
  - wiki-api-authentication
  - wiki-api-fanout
  - wiki-ignored-yaml-keys
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: surviving a third-party API

**The problem.** A vendor API is infrastructure you do not control. It rate-limits, it returns 503
during their deploy, it holds one decommissioned entity that 404s forever, and it is a URL that
someone could point at your cloud metadata endpoint. `source.reliability` is the envelope every
request runs under, including token and discovery calls.

## Retry and backoff

```yaml
reliability:
  timeoutSeconds: 120
  retry:
    maxAttempts: 4
    baseDelayMs: 500
    maxDelayMs: 30000
    honorRetryAfter: true
```

Backoff is `baseDelayMs * 2^attempt`, capped at `maxDelayMs`. Retries fire on transport failures
(a timeout counts as one) and on the transient statuses 408, 425, 429, 500, 502, 503, 504. Any other
status is permanent and is not retried.

`honorRetryAfter` treats a server `Retry-After` as a **floor** raised to the computed backoff, never
a replacement. That matters in both directions: a CDN edge answering `Retry-After: 0` cannot turn the
retry into a busy loop, and an absurd value is still bounded by `maxDelayMs`.

**These four keys are the whole retry surface.** A key like `backoffSeconds` looks plausible and does
nothing, silently, because the YAML loader ignores unmatched properties. See
[ignored-yaml-keys](../incidents/ignored-yaml-keys.md), which documents a live production flow with
exactly that mistake.

## Rate limiting and concurrency

`rateLimitRps` is an outbound token bucket with fractional refill, applied to every request including
token and discovery calls. `concurrency` bounds how many requests are in flight at once. A wide
fan-out that respects neither is the fastest way to get an integration throttled or blocked.

Set `rateLimitRps` from the vendor's published limit, not from what you can get away with.

## Tolerating dead entities

A wide `date x id` sweep routinely carries ids the endpoint no longer accepts: a decommissioned
route, a closed account. Without help, one such id fails the whole sweep, every run, forever.

```yaml
reliability:
  skipStatusCodes: [404, 410]
```

A listed non-retryable status marks that single request a tolerated **skip** rather than a failure.
Each skip is logged as `acquire.skip` with the endpoint's own error body and counted in the run's
`skippedRequests`, so a newly-broken id stays visible instead of being swallowed. Only non-2xx codes
in 100-599 are accepted, and the retry policy still runs first, so a transient 429 retries before it
can ever be skipped.

This is per-request tolerance. For a clean end-of-sequence signal, use `stopOnStatus` in
[api-pagination](api-pagination.md) instead.

## The SSRF guard, and what the allowlist does not cover

`urlAllowlist` restricts data-request hosts: `*.suffix` matches the bare domain and any subdomain,
anything else is an exact host.

Independently of that list, and not disableable by it, private, loopback, link-local (including the
`169.254.169.254` cloud metadata address), multicast, and unique-local addresses are **always**
blocked. The allowlist can narrow what is reachable; it can never widen it to include those.

Token and discovery calls keep the IP guard but skip the host allowlist, because they are trusted
configuration rather than data endpoints. So an allowlist entry is never the fix for a failing token
request.

`maxResponseBytes` caps a single response held in memory before landing, failing the run with a
message naming the limit rather than exhausting the node. `verifyTls: false` exists for a source with
a genuinely broken chain you have decided to trust, and should be treated as a deliberate, reviewed
exception.

## Character encoding

`request.responseCharset` forces the decoding of a response whose declared charset is wrong or
missing. Norwegian sources served as Latin-1 but labelled UTF-8 arrive mangled otherwise, and the
mangling survives all the way into the warehouse where it is expensive to find. Exemplars:
`kommunedata/`, `svv/`.

## Observability

Response headers written by `landing.persistHeaders` have sensitive headers (`authorization`,
`cookie`, `x-api-*`) redacted before the sidecar is written, so header capture is safe to leave on.

## Production exemplars

| Flow folder | Pattern |
| --- | --- |
| `Norled/` | `skipStatusCodes` for retired routes in a wide sweep |
| `questback/` | `maxResponseBytes`, explicit retry, `urlAllowlist` |
| `svv/`, `kommunedata/` | `responseCharset` against mislabelled encodings |
| 15 folders | `rateLimitRps` + `urlAllowlist` as the baseline envelope |
