---
id: concept-external-triggers
title: "External triggers: sync a repository and fire its schedules from outside SQLFlow"
type: concept
summary: How a GitHub workflow or a key holder gets a fenced credential and asks to sync a repository and fire its schedules, and every way a request ends.
keywords:
  - external trigger
  - trigger request
  - github actions
  - oidc
  - openid connect
  - workload identity
  - trigger key
  - sqlft_
  - sync and fire
  - merge
  - commit
  - request key
  - idempotency
  - cancel
  - job summary
  - ControlPlane:ExternalTriggers
  - ControlPlane:Bootstrap:ExternalTriggers
related:
  - guide-github-actions
  - concept-authentication-and-identity
  - concept-control-plane
  - flow-schedule
  - concept-maintenance
sourceRefs:
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/ExternalTriggerStore.cs
  - src/SqlFlow.Catalog/TriggerRequestStore.cs
  - src/SqlFlow.Catalog/RepoSourceStore.cs
  - src/SqlFlow.ControlPlane/Api/ExternalTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/TriggerRequestEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/TriggerRequestSummary.cs
  - src/SqlFlow.ControlPlane/Background/TriggerRequestProcessor.cs
  - src/SqlFlow.ControlPlane/Background/TriggerRequestService.cs
  - src/SqlFlow.ControlPlane/Background/CommitContainment.cs
  - src/SqlFlow.ControlPlane/Background/RunOutcomeDescriber.cs
  - src/SqlFlow.ControlPlane/Background/BootstrapProvisioningService.cs
  - src/SqlFlow.ControlPlane/Security/ExternalTriggerCredential.cs
  - src/SqlFlow.ControlPlane/Security/GitHubActionsTokenValidator.cs
  - src/SqlFlow.ControlPlane/Security/TokenIssuer.cs
  - src/SqlFlow.ControlPlane/Security/AccessTokenGenerator.cs
  - src/SqlFlow.ControlPlane/Configuration/ExternalTriggerOptions.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneModuleServices.cs
  - gui/src/features/external-triggers/ExternalTriggersPage.tsx
  - gui/src/features/external-triggers/workflow.ts
  - integrations/github/action.yml
  - integrations/github/lib.mjs
---

# External triggers

An external trigger is a caller outside SQLFlow that may **sync one repository and fire a selection of its schedules**:
a GitHub Actions workflow right after a merge, or another system that holds a key. It is not a person and holds no
role. It proves who it is, gets a short-lived credential that reaches only its own requests, and makes **trigger
requests**: "sync this repository (at this commit), then fire these schedules". The control plane answers at once and
moves each request along in the background; the caller follows it, streams its trace into its own log, and posts its
summary where it reports. A GitHub job does all of that through SQLFlow's action ([the guide](../guides/github-actions.md)).

An administrator manages triggers on the **External triggers** page (Admin), or the configuration names them
([below](#configured-triggers)). The same page lists every request with its live trace.

## How a trigger proves who it is

| Kind | Proof | What is stored |
| --- | --- | --- |
| `github` (recommended) | The OpenID Connect token GitHub mints for the job (`permissions: id-token: write`), for the audience this control plane names. | Nothing secret: the repository, ref, workflow and environment the token must state. |
| `key` | A key of SQLFlow's form, `sqlft_` and 43 random characters (256 bits), minted on the page and shown once, or one the configuration names by reference. | The key's SHA-256 hash and its first 12 characters. |

A trigger keeps the kind it was created with (only the configuration may change a configured trigger's). Each kind
exchanges its proof for a credential at an anonymous endpoint:

- `POST /api/v1/auth/trigger/github` with `{ "token": "<the job's token>", "trigger": "<name, when several trust the job>" }`
- `POST /api/v1/auth/trigger/key` with `{ "key": "sqlft_..." }`

Both answer `{ accessToken, tokenType: "Bearer", expiresIn, expiresUtc, trigger: { id, name, repoName, allSchedules,
schedules } }` with `Cache-Control: no-store`, and record when the trigger was last used and by what (for GitHub: the
repository and its id, the workflow, the ref and commit, the run and attempt, the environment and the actor).

### Checking a GitHub token

`GitHubActionsTokenValidator` checks, in this order, and stops at the first failure with a reason the workflow's author
can act on:

1. The token is a JSON web token issued by `ControlPlane:ExternalTriggers:GitHub:Issuer` (github.com's
   `https://token.actions.githubusercontent.com` by default). A token from another issuer is named for what it is, before
   any key is read.
2. Its audience is `ControlPlane:ExternalTriggers:GitHub:Audience`; otherwise the answer names the audience to ask for.
3. Its signature is RS256 under one of the keys the issuer publishes (`<issuer>/.well-known/openid-configuration`).
   A key the cached set does not hold triggers one refresh (key rollover); a key still unknown is a forgery. HS256 and
   unsigned tokens are refused.
4. It has not expired (30 seconds of clock skew) and it states a `repository` and a `ref`.

GitHub's keys being unreadable is an outage, answered 503 so the caller retries; every other failure is 401.

The token is then matched to the **enabled GitHub triggers of its repository** (`repository` compared without regard to
case). A trigger trusts the job when:

| Trigger field | Token claim | Rule |
| --- | --- | --- |
| Repository id (optional) | `repository_id` | Equal, when pinned. Pinning refuses a repository deleted and recreated under the same name. |
| Ref | `ref` | Matches the pattern; `*` matches any run of characters (`refs/heads/release/*`), and `*` alone any ref. A bare branch is stored as `refs/heads/<branch>`. |
| Workflow (optional) | `workflow_ref` | The workflow file's path in the repository is equal (`.github/workflows/deliver.yml`). Unnamed trusts every workflow of the repository. |
| Environment (optional) | `environment` | Equal: the environment's protection rules (required reviewers, branch rules) then apply before the job gets a token the trigger accepts. |

Exactly one trigger must trust the job, or the caller names one. None answers 403, listing each trigger of the job's
repository and which field did not match (triggers of other repositories are never named); several answer 409 asking for
a name. A trigger the job names but that does not trust it answers 403 with the field.

## The trigger's credential

The exchange issues an HS256 token signed like every other SQLFlow token (`TokenIssuer.IssueTriggerCredential`): subject
`trigger:<name>`, `token_use: trigger`, the trigger's id, no scope, no role, no user and no `auth_time`, valid for
`ControlPlane:ExternalTriggers:TokenMinutes` (60 by default, 5 to 1440). A caller that follows a request longer exchanges
again: a GitHub job can ask GitHub for a new token at any time during the job.

Every signed-in principal may read and operate the product, so `ExternalTriggerCredentialMiddleware` refuses this
credential (403, `Not available to an external trigger`) on every endpoint not opened to it. The ones opened are the
trigger request endpoints below, and each of them reads only the requests the trigger itself made: another trigger's
request answers 404. The credential cannot renew itself, read the catalog or the runs, fire a schedule directly, mint a
personal access token or reach an admin endpoint. The rate limiter gives each issued credential a window of its own, so
parallel jobs acting as one trigger never share one.

Disabling or removing a trigger refuses its next exchange and its next request at once; a request it already made
carries on, and stays readable with a credential issued before.

## Trigger requests

`POST /api/v1/trigger-requests` makes a request. An external trigger and a person (any signed-in user, who may name any
repository and schedule) make them the same way:

| Field | Meaning |
| --- | --- |
| `repo` | The repository (its repo source's name). An external trigger's own when omitted; another is refused (403). |
| `commit` | The commit the repository must be synced at: the merge being reported. 7 to 64 hexadecimal characters. Omitted takes whatever the sync pulls. |
| `sync` | Whether to sync first; `true` by default. `false` fires on the commit already synced and takes no commit. |
| `schedules` | The schedules to fire once the sync has run, in order, at most 50. None only syncs. An external trigger may name only the schedules it may fire. |
| `batch` | The `batch:` tags every fire is narrowed to, as a schedule's run-now narrows it; needs `schedules`. |
| `chain` | Whether each fire carries the schedules chained behind it (`true` by default, as the schedule's own clock does). |
| `requestKey` | The caller's own key for the request, at most 200 characters. |

A request is answered **202** with the request (`Location: /api/v1/trigger-requests/{id}`), or **200** with the request
already made when the caller repeats a `requestKey` it used before: a retried call or a re-run step never fires twice.
The check and the insert share one serializable transaction, so concurrent calls with one key make one request. It is
refused, before anything is recorded, with 400 for a malformed commit, a commit without a sync, a commit on a repo
source that reads a local directory, a batch without schedules or too many schedules; 403 for a schedule or repository
the trigger may not touch (naming the schedules it may fire); 404 for an unknown repository; 409 for a disabled repo
source when a sync is asked for; and 503 while external triggers are turned off.

### How a request moves

`TriggerRequestService` moves every open request along on every replica, every `PollSeconds` (2 by default) and at once
when one is made. A request is leased to one replica at a time and the lease is renewed right before each fire, so two
replicas never fire one schedule; a replica that stops leaves its requests to the others once their leases lapse.

1. **Syncing.**
   - Without a sync, the request fires on the commit the repository is synced at (`syncOutcome: skipped`).
   - When the repository is already synced at the commit asked for (a short id matches as a prefix of at least 7
     characters), no sync runs (`current`).
   - Otherwise the request asks for a sync of the repo source (the same claim the managed sync and sync-now use, without
     the full lineage recompute a manual sync-now forces) and wakes this replica's sync loop. The repository's own sync
     trace records who asked.
   - It waits for an attempt that started after it asked to record an outcome. The attempt's lines are copied into the
     request's trace as they appear, each once, at most 500, under the step `sync:<the sync's step>`.
   - A failed sync fails the request with the sync's error (`failed`). A source disabled or removed while waiting fails
     it. No answer within `SyncTimeoutMinutes` (30 by default) fails it as `timed-out`, saying when this control plane
     runs no managed sync.
   - With a commit asked for and a different commit synced, the request asks git whether the synced commit contains
     it (the history clone, fetched now, with the repository's own credential). It does when the branch moved on past
     the merge, and the request goes on. It does not when the commit is on another branch or was force-pushed away,
     and the request fails, naming the branch the repository syncs. A commit the repository does not hold fails it too.
2. **Firing.** Each requested schedule, in order, through the one fire path the scheduler and run-now use
   ([schedule](../flow/schedule.md)), its runs recording `trigger:<name>` (or the person) as who asked:
   - A schedule the synced commit does not define does not fire; the request lists the repository's schedules.
   - A schedule disabled in its YAML, or paused by an operator, does not fire.
   - One with nothing runnable (in the batch asked for) does not fire.
   - With `chain: false`, the schedules chained behind it are held, as a run-now with `chain=false` holds them.
   - A replica that takes over a schedule found mid-fire first looks for the runs its fire queued (the schedule's runs
     this request asked for, queued since the fire began) and follows those instead of firing again.
3. **Running.** Each run's start and end are written to the trace exactly once, whichever replica sees them: how long it
   took and what it did ([described](#what-a-run-did)), or the first line of its error, redacted. A schedule ends
   `succeeded` when every run it started succeeded, `failed` otherwise (a skipped or cancelled run, or one no longer in
   the catalog, counts against it), with its counts kept on the request.
4. **Ended.** `succeeded` when the sync went through, every schedule fired and every run succeeded; `failed` otherwise,
   with one sentence saying which schedules did not fire or did not succeed; `cancelled` when someone cancelled it.

A pass that fails (an unreadable row, a git outage) is written to the request's trace and tried again; after 20 failed
passes in a row on one replica the request ends `failed` with the last error, and its schedules that never fired say so.

### Following a request

| Endpoint | Answers |
| --- | --- |
| `GET /api/v1/trigger-requests/{id}` | The request and every run its fires started, read live: state, `conclusion` (`success`, `failure`, `cancelled`, or null while it runs), the commits, the sync's outcome and error, each schedule with its run or group, commit and counts, each run with its wave, status, times, what it did and its error's first lines, and links into the GUI when `ControlPlane:Notifications:GuiBaseUrl` is set. |
| `GET /api/v1/trigger-requests/{id}/events?after=<id>&limit=<n>` | The request's trace after a cursor, at most 500 events a page, the cursor to ask from next, and `done` once the request has ended and every event has been read. |
| `GET /api/v1/trigger-requests/{id}/summary` | The request as Markdown (`text/markdown`): the outcome in the heading, the commits and the sync in a table, each schedule with every flow it ran (wave, status, how long, what it did), the errors in full and the sync's lines folded away. Text a run or a sync wrote is escaped, so it cannot break the layout. |
| `GET /api/v1/trigger-requests/{id}/runs/{runId}/trace` | One run's whole trace as text, for a run this request started. |
| `POST /api/v1/trigger-requests/{id}/cancel` | Asks to cancel: the request stops waiting, fires nothing more and cancels the runs it fired (a running run is asked to stop). 409 once it has ended. |
| `GET /api/v1/trigger-requests` | The requests, newest first, by `triggerId`, `repo` and `state`. An external trigger sees only its own. |

The request's trace is an activity trace (kind `trigger-request`, subject the request's id), so the GUI's bottom panel
streams it like a repository sync's.

### What a run did

The trace and the summary describe each succeeded run in a few words: the first `IRunOutcomeDescriber` with something to
say about it, otherwise its row counts (`1,234 rows loaded (1,000 inserted, 234 updated)`). A host module registers one
for its own flow kinds with `AddRunOutcomeDescriber<T>()`; it reads the run's recorded result. A describer that throws is
logged and passed over.

## Configured triggers

`ControlPlane:Bootstrap:ExternalTriggers:<name>` declares a trigger, written at every start by the `external-triggers`
start-up step and refused every change through the API (409, naming the setting):

```text
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__RepoSource=estate
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__Schedules__0=nightly
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__GitHub__Repository=acme/estate
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__GitHub__RepositoryId=123456789
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__GitHub__Ref=refs/heads/main
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__GitHub__Workflow=.github/workflows/sqlflow.yml
ControlPlane__Bootstrap__ExternalTriggers__estate-deliver__GitHub__Environment=production
```

Each takes `RepoSource`, `Description`, `Schedules` or `AllSchedules`, and exactly one of `GitHub` (`Repository`,
`RepositoryId`, `Ref`, `Workflow`, `Environment`) and `KeyReference`: a `${keyvault:vault/secret}` or `${env:NAME}`
reference to a key of SQLFlow's form (`sqlft_` and at least 32 random characters), never the key itself. The key is read
at every start, so rotating the secret and restarting rotates it; a secret holding anything else fails the start-up step.
A trigger the configuration stops naming is disabled and released (kept, never deleted, so its requests stay attributed).
Each definition is checked by the same rule the API applies, and a bad one stops the control plane at start with the
setting's name.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `ControlPlane:ExternalTriggers:Enabled` | `true` | Off, every exchange and request answers 503 and open requests wait. Nothing is trusted until a trigger exists, so the default opens nothing. |
| `ControlPlane:ExternalTriggers:TokenMinutes` | `60` | How long an exchanged credential lives (5 to 1440). |
| `ControlPlane:ExternalTriggers:SyncTimeoutMinutes` | `30` | How long a request waits for its sync (1 to 1440). |
| `ControlPlane:ExternalTriggers:PollSeconds` | `2` | How often open requests are moved along (1 to 60). |
| `ControlPlane:ExternalTriggers:GitHub:Issuer` | `https://token.actions.githubusercontent.com` | The issuer GitHub signs Actions tokens as; change it for GitHub Enterprise Server (`https://HOST/_services/token`). |
| `ControlPlane:ExternalTriggers:GitHub:Audience` | `sqlflow` | The audience a workflow must ask GitHub's token for. Give each deployment its own (its address is a good choice), so a token minted for one is refused by the others. |
| `ControlPlane:ExternalTriggers:GitHub:ServerUrl` | `https://github.com` | Where the links back to a workflow run point. |
| `ControlPlane:ExternalTriggers:GitHub:ActionReference` | `./.github/actions/sqlflow` | What the workflow the External triggers page writes out names in `uses:`: a copy of the action in the workflow's repository by default, or one an organization keeps for every repository. |

## Where it is kept

Triggers are in `catalog.ExternalTrigger` and requests in `catalog.TriggerRequest`, `catalog.TriggerRequestSchedule` and
`catalog.TriggerRequestRun` (the link from each run back to its request). On the [Maintenance](maintenance.md) page they
are the areas `sqlflow.external-triggers` and `sqlflow.trigger-requests`, both kept; a request's trace is one activity
in `sqlflow.activity-traces`, kept as long as the request.

## See also

- [Syncing and firing from GitHub](../guides/github-actions.md)
- [Authentication and identity](authentication-and-identity.md)
- [schedule](../flow/schedule.md)
