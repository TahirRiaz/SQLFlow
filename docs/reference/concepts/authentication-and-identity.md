---
id: concept-authentication-and-identity
title: "Authentication and identity: local login, bootstrap tokens, Entra SSO"
type: concept
summary: How the control plane signs users in (local, Entra SSO, bootstrap secret), issues HS256 tokens with scopes, and administers users and roles.
keywords:
  - jwt
  - login
  - bootstrap secret
  - entra
  - sso
  - msal
  - roles
  - scopes
  - throttle
related:
  - concept-control-plane
  - guide-deployment
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/AuthEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/UserEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/Contracts.cs
  - src/SqlFlow.ControlPlane/Security/TokenIssuer.cs
  - src/SqlFlow.ControlPlane/Security/LoginThrottle.cs
  - src/SqlFlow.ControlPlane/Security/EntraTokenValidator.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.ControlPlane/Program.cs
  - src/SqlFlow.Catalog/UserStore.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
---

# Authentication and identity

The control plane offers several ways to sign in and issues one predominant kind of credential out of them: an HS256 SQLFlow bearer token. Because every token-minting path converges on the same token format and the same claims, authorization downstream is identical regardless of how the caller authenticated. Three of the paths are the interactive sign-in surfaces mapped under `/api/v1` (src/SqlFlow.ControlPlane/Api/AuthEndpoints.cs):

| Endpoint | Who | Availability |
| --- | --- | --- |
| `POST /api/v1/auth/login` | A local SQLFlow user (username and password held in the catalog) | Always mapped |
| `POST /api/v1/auth/exchange` | Microsoft Entra ID single sign-on (the SPA exchanges an Entra ID token) | Mapped when Entra is enabled (automatic once `AllowedTenantIds` and `ClientId` are set) |
| `POST /api/v1/auth/token` | The break-glass bootstrap secret | Mapped only when `ControlPlane:Jwt:BootstrapSecret` is set |

`GET /api/v1/auth/providers` is anonymous and tells the login page which of these are available. It returns `{ local, bootstrap, entra }`; when Entra is enabled, the `entra` object carries the `clientId` and resolved `authority` (see [More than one tenant](#more-than-one-tenant) for which authority that is) so the login page can configure MSAL without any client-side configuration file. All sign-in responses are sent with `Cache-Control: no-store` and `Pragma: no-cache` so an intermediary can never cache a bearer token.

Two further sign-in surfaces exist for clients that cannot drive an interactive login page:

- **The OAuth 2.0 device-authorization grant (RFC 8628)**, always mapped, is the path for the MCP server and any headless client (src/SqlFlow.ControlPlane/Api/AuthEndpoints.cs:51-72,255,290-333). `POST /api/v1/auth/device` mints a device/user code pair and advertises the approval URL; both start and `POST /api/v1/auth/device/token` polling are anonymous, since the opaque `device_code` is the only secret the polling client holds. Approval and denial (`POST /api/v1/auth/device/approve`, `POST /api/v1/auth/device/deny`) run under an authenticated browser session (the `read` scope), so a human binds their own identity to the device. Once approved, the poll mints a normal HS256 token for the approving user, scoped to the granted subset of the device's allowed scopes `read` and `operate`: `admin` is deliberately excluded, so a headless client can never obtain account-administration rights this way.
- **Personal access tokens (PATs)** are a distinct, long-lived bearer credential for headless clients (the CLI, the VSCode extension, automation), rather than a short-lived session token. A PAT and an HS256 token share one `Authorization: Bearer` header; a policy scheme (`PersonalAccessTokenDefaults.PolicyScheme`) inspects the presented token and forwards it to the right validator by shape, so authorization downstream sees one authenticated principal type regardless of which credential arrived (src/SqlFlow.ControlPlane/Program.cs:123-140,280). PATs are self-managed: any authenticated user creates, lists, and revokes their own through the `/me` endpoints, with each token's scopes capped server-side to the caller's own and the secret shown exactly once at creation and never retrievable again (src/SqlFlow.ControlPlane/Api/Contracts.cs:103-114).

## Tokens, scopes, and roles

`TokenIssuer` (src/SqlFlow.ControlPlane/Security/TokenIssuer.cs) signs tokens with HS256 using `ControlPlane:Jwt:SigningKey`, which must be at least 32 bytes; a shorter or missing key fails startup validation. Every token carries:

- `sub`: the subject (username, or the requested bootstrap subject, default `bootstrap`)
- `jti`: a unique token id
- `scope`: a space-delimited scope list, for example `read operate admin`
- `iss` / `aud`: `ControlPlane:Jwt:Issuer` (default `sqlflow-control-plane`) and `ControlPlane:Jwt:Audience` (default `sqlflow`)

User-backed tokens (local login and Entra exchange) additionally carry `role` (the role name) and `uid` (the catalog user id) so the GUI can shape itself without a second call, plus `auth_time`: when the user actually proved who they are. Bootstrap tokens carry none of these. Expiry is `ControlPlane:Jwt:AccessTokenMinutes` (default 720, valid range 1 to 1440).

## Sessions roll, they do not expire under the user

`AccessTokenMinutes` is one token's life, not the user's. A signed-in GUI trades its token for a fresh one at `POST /auth/renew` before the current one lapses, and again whenever SQLFlow is opened, so the person stays signed in for as long as they keep working while any single issued token (including a leaked one) stays short-lived. Ticking "Keep me signed in on this device" at sign-in puts the session in `localStorage` rather than `sessionStorage`, so it survives a browser restart and can roll on the next visit.

That tick is itself remembered, alongside the subject of the last successful sign-in, under the separate `sqlflow.login` key in `localStorage` (gui/src/auth/loginPrefs.ts). The login page prefills both, and puts the caret on the password when it does. These outlive a sign-out on purpose: a session ending is precisely when someone would otherwise have to retype their own name, and it is what stops the "keep me signed in" choice from silently reverting to unchecked on every visit. No secret is kept there, only the subject and the flag, so the password, the token, and the bootstrap secret remain governed solely by the storage rules above.

`beginSession` records it, from the `subject` the server returned rather than from the typed field, so one line covers local and Entra sign-in alike (the latter types no username at all) and a local sign-in records the account's canonical name rather than whichever casing was typed. Only a renewable (user-backed) session writes a prefill, so the break-glass path leaves no trace, and because `beginSession` only runs once the server has accepted a credential, a failed sign-in never becomes a prefill. Note that the subject deliberately does **not** come from decoding the session token: by the time the login page renders there is no token left to decode, since sign-out, expiry, and a 401 all wipe it, and retaining a dead-but-possibly-still-valid bearer token purely to read a name out of it would be a worse trade than storing the name.

The call is authenticated by the very token it replaces, so there is no second long-lived refresh credential to store or leak. Three things bound it:

- **`auth_time`**: carried unchanged through every roll, so the cap below is measured from the real sign-in and not from the newest token.
- **`ControlPlane:Jwt:SessionMaxDays`** (default 30): past this, renewal is refused and a real sign-in is the only way back.
- **A re-read of the account on every roll**: deactivating a user, changing their role, or deleting the role takes effect at their next renewal instead of lingering for the life of an issued token.

Renewal is refused outright (403) for any credential with no `auth_time`: a personal access token (which already carries its own lifetime), the break-glass bootstrap token, and the device grant, whose long-lived path is a personal access token instead.

Authorization has exactly two tiers, defined in src/SqlFlow.ControlPlane/Program.cs. Any authenticated caller gets the whole operational product: reading (catalog, runs, lineage, search, schedules, nodes, repo sources, summary), triggering and cancelling runs, schedule and repo-source writes, and proposing pipelines to a repo source as a pull request. Only user and role administration is fenced off, behind the `admin` scope. So the `read`, `operate`, and `author` policies all resolve to "authenticated", and `admin` alone consults the token's `scope` claim. This is deliberate: a signed-in user is never stuck unable to use a feature the UI shows them, and elevating an account is only ever needed to let it administer other accounts. The `operate` and `author` scopes still exist on tokens and roles (a token minted with only `read` is perfectly usable across the operational surface), but no policy other than `admin` enforces a scope.

Roles are catalog rows mapping a name to a scope string. `BootstrapProvisioningService` (src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs) seeds the built-in roles at startup, idempotently and without overwriting operator edits:

| Role | Scopes |
| --- | --- |
| `admin` | `read operate admin author` |
| `operator` | `read operate author` |
| `viewer` | `read` |

Because `BootstrapProvisioningService` seeds roles without overwriting existing rows, a catalog first provisioned before `author` existed keeps its old scope string; grant `author` to the `operator` / `admin` roles there (a role scope edit) to enable pull-request authoring.

When a session is issued, the user's role row is loaded and its scopes become the token's `scope` claim. If the role row no longer exists, sign-in fails closed with 403 `Role is not provisioned`; a user whose role was deleted never receives a fallback grant.

## Local login

`POST /api/v1/auth/login` takes `{ "username": "...", "password": "..." }` and returns a session:

```json
{
  "accessToken": "eyJhbGciOiJIUzI1NiIs...",
  "tokenType": "Bearer",
  "expiresIn": 3600,
  "subject": "ops.lead",
  "role": "admin",
  "scopes": ["read", "operate", "admin"]
}
```

Passwords are hashed with the ASP.NET Identity `PasswordHasher` (PBKDF2). When a login verifies against an older hash format, the hash is transparently upgraded in place (rehash-on-login); a failed rehash is logged and the old, still valid hash remains.

Two defenses protect the endpoint:

- **Timing hardening.** Exactly one password hash is always verified per attempt. When the username does not exist, the account is inactive, or the account is an SSO account with no password, a decoy hash is verified instead, so response time does not reveal whether a username exists. Both outcomes return the same generic 401 `Invalid username or password`.
- **Per-pair throttling.** `LoginThrottle` (src/SqlFlow.ControlPlane/Security/LoginThrottle.cs) tracks consecutive failures per (username, client IP) pair: 5 failures within a 900 second window lock the pair out for 300 seconds, during which login attempts get HTTP 429 `Too many failed sign-in attempts`. A successful sign-in clears the pair's history. The throttle is in-memory per node and complements the global per-IP rate limiter. Behind a reverse proxy, enable `ControlPlane:Proxy` with trusted `KnownNetworks`/`KnownProxies` so the client IP is the real caller rather than the proxy.

## Bootstrap tokens

`POST /api/v1/auth/token` is the break-glass path for a fresh deployment or automation that has no user yet. The endpoint exists only when `ControlPlane:Jwt:BootstrapSecret` is configured (at least 32 UTF-8 bytes, enforced at startup). The caller presents the exact secret, compared in fixed time:

```json
{
  "secret": "the-configured-bootstrap-secret-value",
  "subject": "ci-pipeline",
  "scopes": ["read", "operate"]
}
```

Requested scopes are filtered against the allowed set `read`, `operate`, `author`, `admin`; omitting `scopes` defaults to `["read"]`. If no requested scope is valid, the response is 400 `No valid scope requested` listing the allowed scopes. A wrong secret returns 401 `Invalid bootstrap secret`. The response is `{ accessToken, tokenType, expiresIn }` with no role or uid claim.

## Microsoft Entra ID single sign-on

Entra single sign-on turns on automatically once `ControlPlane:AzureAd:AllowedTenantIds` (at least one entry) and `ControlPlane:AzureAd:ClientId` are configured: supplying both credentials is the whole switch, with no separate enable flag to remember. (Set `ControlPlane:AzureAd:Enabled` explicitly only to override the default: `true` requires SSO and fails startup if a credential is missing, `false` forces it off even when credentials are present.) Local username/password sign-in is always available alongside it; enabling Entra only adds the "Sign in with Microsoft" option. When enabled, the SPA signs the user in against Entra with MSAL (authorization code + PKCE) and posts the resulting ID token to `POST /api/v1/auth/exchange` as `{ "token": "..." }`. The control plane validates the Entra token and issues its own SQLFlow session token, so downstream API calls use one token type regardless of sign-in method.

### More than one tenant

`AllowedTenantIds` is a list, not a single value, so an estate can trust users from more than one Entra tenant at once (for example, the app registration's own home tenant plus a customer organization's corporate tenant). Two things have to agree for a foreign tenant to actually work:

- **The app registration itself must be multi-tenant** (`signInAudience: AzureADMultipleOrgs`). With more than one allowed tenant, MSAL authenticates the SPA against the shared `organizations` authority (`ResolveAuthority()`, src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs), which accepts a sign-in attempt from any Entra work/school tenant; a single-tenant app registration rejects a foreign account at Microsoft's own login page (`AADSTS650059`) before a token is ever issued, regardless of what the control plane is configured to allow.
- **Each additional tenant's own admin must consent to the app once.** Consent is what creates that tenant's own local enterprise-application object; without it, Entra refuses to issue a token for that tenant at all. If the deployment also enforces app-role assignment (see below), that tenant's admin then assigns the role to their own users or groups, entirely within their own directory — this deployment's credentials never reach into a foreign tenant.

The control plane's `AllowedTenantIds` is the actual trust boundary on the backend: a multi-tenant app registration will let *any* consenting tenant obtain a token, but `EntraTokenValidator` only accepts one whose `tid` claim is in this list, so unnamed tenants are rejected token by token even after they've consented to the app.

**Adding a second tenant changes how existing B2B guests sign in, and that is usually the deciding factor.** With exactly one allowed tenant the authority is pinned to it, so an external address (a consultant's `@partner.com`) resolves as a *guest of that tenant*: Entra delegates the password check to their home tenant but issues the token from the pinned one, needing no consent anywhere and no separate credential. Adding a second tenant forces the shared `organizations` authority, and the same address now resolves to the person's *own home tenant*, where this app is unknown; they get an admin-approval wall, and their guest identity no longer applies. So a estate whose external users are guests should stay single-tenant and invite people as guests, which needs no cooperation from any outside directory. Reach for a second tenant only when a whole partner organization should sign in with its own accounts, and accept that each such tenant's admin must consent and assign the app role themselves.

`deploy/bicep/main.bicep` automates this: `provisionEntraApp` (default on) creates the app registration via `entra-app.bicep`, always trusting the deployment's own subscription tenant; `azureAdAdditionalAllowedTenantIds` names further tenants, which automatically flips the registration to multi-tenant and is surfaced back as the `entraForeignTenantReminder` output (the manual consent-then-assign step each named tenant's admin still has to do by hand).

`EntraTokenValidator` (src/SqlFlow.ControlPlane/Security/EntraTokenValidator.cs) reads the token's own `tid` claim first (unvalidated) to pick which allowed tenant to validate against — rejecting outright, before any metadata fetch, if `tid` is not in `AllowedTenantIds` — then pins validation to that tenant specifically:

- **Issuer**: that tenant's own issuer, taken from its OIDC metadata at `{TenantAuthority(tid)}/.well-known/openid-configuration`, so a token asserting any other tenant (even an allowed one, if it lied about `tid`) fails signature/issuer validation against the metadata actually fetched
- **Audience**: `ControlPlane:AzureAd:ClientId`
- **Signature**: that tenant's JWKS, RS256 only, with a single refresh-and-revalidate when the key is unknown (key rollover); a still-unknown key after refresh is an untrusted token. Each allowed tenant gets its own cached metadata manager, refreshed independently.
- **Lifetime**: with 30 seconds of clock skew

Any validation failure returns 401 `Invalid identity token`. An OIDC metadata outage (network failure, misconfigured authority) is logged as an outage and fails closed with the same 401; it never falls back to cached-forever keys or unsigned acceptance.

Users are keyed on the immutable `oid` claim, so a UPN rename never duplicates a user. The sign-in name is taken from `preferred_username`, falling back to `email` then `upn`. First sign-in provisions the user just in time with `ControlPlane:AzureAd:DefaultRole` (default `viewer`, least privilege; an admin raises it afterwards). Exchange-specific errors:

| Status | Title | Meaning |
| --- | --- | --- |
| 403 | `Account is deactivated` | The account exists but an administrator deactivated it |
| 409 | `Sign-in name already in use` | A different account already uses this sign-in name; an administrator must resolve it |
| 500 | `Identity is not provisioned` | The configured default role does not exist; bootstrap provisioning has not completed |

Because the Entra token already proved who the caller is, these account-state responses are safe to disclose (unlike local login's generic 401).

## User and role administration

The admin surface (src/SqlFlow.ControlPlane/Api/UserEndpoints.cs) requires the `admin` scope:

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/users` | Paged list, filterable by `username`, `role`, `provider`, `active` |
| `GET /api/v1/users/{id}` | One user |
| `POST /api/v1/users` | Create a local user (`{ username, password, role, email, displayName }`) |
| `POST /api/v1/users/{id}/role` | Change role (`{ role }`) |
| `POST /api/v1/users/{id}/activate` | Reactivate |
| `POST /api/v1/users/{id}/deactivate` | Deactivate |
| `POST /api/v1/users/{id}/password` | Reset a local user's password (`{ password }`) |
| `GET /api/v1/roles` | List roles with their scopes |

Rules enforced by the endpoints and the store:

- Local passwords must be at least 12 characters (`UserEndpoints.MinPasswordLength`); shorter ones get 400 `Password too short`. Length is the only composition rule; character-class requirements are deliberately not imposed.
- There is no user delete. Deactivation is the removal, so run history stays attributable.
- The last active admin can never be demoted or deactivated; the attempt gets 409 `Last active admin`, so the catalog cannot be administered into a lockout.
- Setting a password on an SSO account returns 409 `Not a local user`; that credential lives in Entra.
- Creating a user with an unknown role returns 400 `Unknown role`; a duplicate username returns 409 `Username already in use`.

SSO users never appear via `POST /users`; they are provisioned by their first sign-in and only governed (role, active) here.

## First-run provisioning

`BootstrapProvisioningService` runs at startup and retries with backoff until the catalog database is reachable, so a control plane that starts before its database converges instead of crashing. In order, and all idempotently: it applies pending catalog migrations (unless `ControlPlane:Bootstrap:ApplyMigrations` is false, in which case pending migrations are logged as a warning), seeds the three built-in roles, and creates the initial admin when `ControlPlane:Bootstrap:AdminUsername` and `ControlPlane:Bootstrap:AdminPasswordReference` are configured and the user is absent. The password reference is a secret reference (`${env:...}` or `${keyvault:...}`) resolved through the SqlFlow secret resolver, and it only seeds the first credential; an existing admin's password is never reset by configuration. An admin password shorter than 12 characters or an empty resolved secret aborts admin creation with an error log, and the two bootstrap settings must be set together (startup validation fails otherwise). With no users and no configured admin, a startup warning notes that only the bootstrap secret can access the API.

## Configuration touchpoints

All settings live in the `ControlPlane` configuration section (appsettings or environment variables in the `ControlPlane__Jwt__SigningKey` double-underscore form) and are validated at startup by `ControlPlaneOptions.Validate` (src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs); a misconfigured deployment never starts serving.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ControlPlane:Jwt:Issuer` | `sqlflow-control-plane` | Token issuer claim |
| `ControlPlane:Jwt:Audience` | `sqlflow` | Token audience claim |
| `ControlPlane:Jwt:SigningKey` | none, required | HS256 key, at least 32 bytes, sourced from a secret |
| `ControlPlane:Jwt:AccessTokenMinutes` | `720` | One token's lifetime, 1 to 1440. Not how long a user stays signed in: the GUI rolls its token at `POST /auth/renew` |
| `ControlPlane:Jwt:SessionMaxDays` | `30` | Absolute ceiling on a rolling session, measured from the actual sign-in, 1 to 365 |
| `ControlPlane:Jwt:BootstrapSecret` | unset | Enables `POST /auth/token`; at least 32 bytes when set |
| `ControlPlane:AzureAd:Enabled` | unset (auto) | Explicit override. Unset: SSO auto-enables when `AllowedTenantIds` and `ClientId` are both set. `true`: require SSO (startup error if a credential is missing). `false`: force off even with credentials |
| `ControlPlane:AzureAd:AllowedTenantIds` | none | The Entra tenant(s) whose users may sign in (a list; `__0`, `__1`, ... in env-var form). Setting this and `ClientId` auto-enables SSO. More than one entry requires the app registration itself to be multi-tenant |
| `ControlPlane:AzureAd:ClientId` | none | The SPA app registration; the required token audience; setting this and `AllowedTenantIds` auto-enables SSO |
| `ControlPlane:AzureAd:Authority` | `https://login.microsoftonline.com` | The authority host. Override only for sovereign clouds (e.g. `https://login.microsoftonline.us`) |
| `ControlPlane:AzureAd:DefaultRole` | `viewer` | Role JIT-provisioned Entra users receive |
| `ControlPlane:Bootstrap:ApplyMigrations` | `true` | Whether startup applies pending catalog migrations |
| `ControlPlane:Bootstrap:AdminUsername` | unset | Initial admin sign-in name; set with the password reference |
| `ControlPlane:Bootstrap:AdminPasswordReference` | unset | Initial admin password as a `${env:...}` / `${keyvault:...}` reference |
| `ControlPlane:Proxy:Enabled` | `false` | Honor `X-Forwarded-For` from trusted proxies so the throttle keys on real client IPs |

## Example: bootstrap a fresh deployment and create the first users

```bash
# 1. Get a break-glass admin token with the configured bootstrap secret.
curl -s -X POST https://sqlflow.example.com/api/v1/auth/token \
  -H "Content-Type: application/json" \
  -d '{"secret": "'"$SQLFLOW_BOOTSTRAP_SECRET"'", "subject": "setup", "scopes": ["read", "operate", "admin"]}'
# -> { "accessToken": "...", "tokenType": "Bearer", "expiresIn": 3600 }

# 2. Create a local operator (password must be at least 12 characters).
curl -s -X POST https://sqlflow.example.com/api/v1/users \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"username": "ops.lead", "password": "a-long-strong-passphrase", "role": "operator", "email": null, "displayName": "Ops Lead"}'

# 3. Sign in as that user; the session token carries role and uid claims.
curl -s -X POST https://sqlflow.example.com/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "ops.lead", "password": "a-long-strong-passphrase"}'
```

The end-to-end behavior of this surface (providers discovery, login success and throttle lockout, Entra exchange with JIT provisioning, the admin API, and role seeding) is exercised in tests/SqlFlow.ControlPlane.Tests/IdentityApiTests.cs and tests/SqlFlow.ControlPlane.Tests/UserStoreTests.cs.

## See also

- [Control plane](./control-plane.md)
- [Deployment guide](../guides/deployment.md)
