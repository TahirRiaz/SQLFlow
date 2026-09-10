---
id: wiki-api-authentication
title: "Pattern: authenticating to a third-party API without storing a secret in YAML"
type: pattern
summary: "The five auth shapes, the four token-endpoint variations production forced, and why every credential is a reference rather than a value."
keywords:
  - oauth2
  - client credentials
  - refresh token
  - token exchange
  - bearer
  - basic auth
  - api key
  - key vault
  - oidc discovery
sourceRefs:
  - src/SqlFlow.Acquire/Runtime/AuthResolver.cs
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
  - src/SqlFlow.Yaml/YamlAcquireFlowLoader.cs
referenceRefs:
  - concept-connections-and-secrets
  - concept-authentication-and-identity
related:
  - wiki-api-resilience
  - wiki-api-resume-and-watermarks
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: authenticating to a third-party API without storing a secret in YAML

**The problem.** Every vendor invents its own login. One wants a static key in a header, another
wants OAuth2 client credentials, a third wants a refresh-token exchange against a bespoke endpoint,
and a fourth publishes an OIDC discovery document instead of a token URL. The pipeline definition
has to live in git, so none of them may contribute a literal secret.

**The rule that makes this safe.** `source.auth` never holds a credential value. It holds a
reference (`${env:NAME}` or `${keyvault:vault/secret}`) resolved at run time through the shared
secret chain. A flow file is therefore always safe to commit, and rotating a credential never
touches the pipeline.

## The five strategies

`source.auth.type` selects the shape:

| Type | What it sends | Keys it needs |
| --- | --- | --- |
| `none` | nothing | |
| `api_key_header` | `secretRef` in a named header | `headerName`, optional `valuePrefix` |
| `api_key_query` | `secretRef` as a query parameter | `paramName` |
| `bearer` | `Authorization: Bearer <secretRef>` | `secretRef` |
| `basic` | HTTP Basic | `secondarySecretRef` (user) + `secretRef` (password) |

The three token-based types (`oauth2_client_credentials`, `oauth2_refresh_token`,
`token_exchange`) each perform a token request described by `source.auth.token`, then apply the
returned token to every data request.

```yaml
source:
  transport: http
  baseUrl: https://api.partner.example
  auth:
    type: oauth2_client_credentials
    token:
      url: https://auth.partner.example/oauth/token
      bodyKind: form
      body:
        grant_type: client_credentials
        client_id: ${keyvault:my-vault/vendor-client-id}
        client_secret: ${keyvault:my-vault/vendor-client-secret}
      tokenPath: access_token
```

## The four variations production actually needed

Each of these exists because a real vendor forced it. They are worth knowing because the naive
shape above does not cover them.

**The client id and secret must go in a Basic header, not the body.** Set
`token.basicAuthClient: true`. The engine lifts `client_id` and `client_secret` out of `body` and
sends them as `Authorization: Basic base64(id:secret)` on the token request instead. Exemplar:
`voi/`.

**The whole request body is itself the secret.** Some vendors hand over a pre-formed urlencoded
body. Putting it in Key Vault whole and referencing it with `token.rawBody` avoids splitting it
into fields that must then be reassembled exactly. When `rawBody` is set, `body` is ignored.
Exemplars: `citybike/`, `frida/`.

**There is no token URL, only a discovery document.** Set `token.discoveryUrl` to the
`.well-known/openid-configuration` endpoint; its `token_endpoint` supplies the URL. It takes
precedence over `url`. Exemplar: `frida/`.

**The token is scoped to the window being fetched.** An issuer that mints a token valid only for
the period you asked about breaks a run that acquires one token and then fans out over thirty days.
`token.refreshPerIteration: true` re-acquires per fan-out iteration. Exemplar: `voi/`.

## Applying the token

Two vendors in the estate reject the standard header. `token.applyHeaderName` sets the header the
resolved token goes into (`easypark/` uses the non-standard `X-Authorization`) and
`token.applyPrefix` sets the value prefix. Absent both, the token is applied as
`Authorization: Bearer <token>`.

`token.tokenPath` is a JSON path into the token response, because the field is not always
`access_token`; `idToken` and nested shapes both occur.

## What to check when auth fails

Token and discovery requests keep the IP guard but deliberately skip the `urlAllowlist`, since they
are trusted configuration rather than data endpoints (see [api-resilience](api-resilience.md)).
So an auth failure is never an allowlist problem. Look instead at whether the secret reference
resolved (the run log names the reference, never the value), whether `tokenPath` matches the actual
response shape, and whether the issuer scopes the token per window.

## Production exemplars

| Flow folder | Shape |
| --- | --- |
| `entur/` | `oauth2_client_credentials`, JSON body with `audience` |
| `voi/` | `basicAuthClient`, `refreshPerIteration` |
| `citybike/`, `frida/` | `rawBody` held whole in Key Vault; `frida` also uses `discoveryUrl` |
| `easypark/` | `token_exchange` with a refresh token, applied to `X-Authorization` |
| `billettapp/` | `oauth2_client_credentials`, form body |
| `hjh_bedrifter/` | `api_key_query` |
| `Norled/`, `fjord1/`, `hentmeg/` | `bearer` with a single `secretRef` |
