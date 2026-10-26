---
id: wiki-external-triggers-prove-and-request
title: "An external trigger proves who it is and makes a request it then follows"
type: decision
summary: "Why a GitHub workflow syncs and fires through a federated, fenced credential and a durable request it follows, and what was rejected."
keywords:
  - external trigger
  - trigger request
  - github actions
  - openid connect
  - workload identity
  - federation
  - personal access token
  - fence
  - durable request
  - commit containment
  - job summary
  - check run
  - event webhook
sourceRefs:
  - src/SqlFlow.ControlPlane/Security/GitHubActionsTokenValidator.cs
  - src/SqlFlow.ControlPlane/Security/ExternalTriggerCredential.cs
  - src/SqlFlow.ControlPlane/Security/TokenIssuer.cs
  - src/SqlFlow.ControlPlane/Api/ExternalTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/TriggerRequestEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/TriggerRequestProcessor.cs
  - src/SqlFlow.ControlPlane/Background/TriggerRequestService.cs
  - src/SqlFlow.ControlPlane/Background/CommitContainment.cs
  - src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - src/SqlFlow.Catalog/TriggerRequestStore.cs
  - integrations/github/lib.mjs
related:
  - wiki-the-mcp-server-is-the-assistants-one-definition
referenceRefs:
  - concept-external-triggers
  - guide-github-actions
  - concept-authentication-and-identity
updated: 2026-10-26
---

# An external trigger proves who it is and makes a request it then follows

The need was plain: after a merge to an estate's repository, sync it now, optionally fire a selection of its schedules,
and have the job that merged show what happened. Everything needed already existed for a person (sync-now, run-now,
the run group reads), so the decisions were about **who** may ask, **how** the asking survives, and **where** the
answer goes. The reference ([External triggers](../../reference/concepts/external-triggers.md)) says what was built;
this page says why.

## The caller federates; nothing secret is stored

The easiest path was a personal access token in the workflow's secrets. It was rejected for three reasons, all visible
in the code:

- **The privilege model makes any token all-powerful.** `ControlPlaneHost` resolves the `read`, `operate` and `author`
  policies to "signed in": any user's token may run any flow, change schedules and repository sources, and open pull
  requests. A token in GitHub's secrets would carry all of that to whoever can edit a workflow.
- **It belongs to a person**, lives until someone revokes it, and leaves no trace of which workflow run used it.
- **GitHub already proves who a job is.** Each job may ask for an OpenID Connect token signed by GitHub that names the
  repository (and its immutable id), the ref, the workflow file, the environment, the run and the actor. Validating it
  against GitHub's published keys (`GitHubActionsTokenValidator`, the same metadata-and-rollover approach
  `EntraTokenValidator` takes for sign-in) means no secret exists on either side to leak or rotate.

The trust rule is therefore a set of claims, not a secret: a trigger names the repository, a ref pattern, optionally
the workflow and the environment, and optionally pins the repository's id (a name can be re-registered by someone
else; an id cannot). The refusal names the field that did not match, because the person reading it is the workflow's
author, who holds the token anyway and needs to know what to change. Triggers of other repositories are never named.

A **key** kind remains for systems that cannot present such a token. It is stored as a SHA-256 hash, like a personal
access token, and is exchanged rather than presented on every call, so the long-lived secret crosses the wire once per
credential, not once per poll.

## The credential is fenced, not scoped

Because scopes enforce nothing below `admin`, cutting a trigger's scopes would fence nothing. The control plane already
had the answer twice over: an assistant run's delegated token and a managed worker's credential are each marked with
`token_use` and refused by a default-deny middleware everywhere they have not been opened. The trigger's credential
follows that pattern exactly (`ExternalTriggerCredentialMiddleware`): a new endpoint is closed to it until someone opens
it, and the few that are open check that a request belongs to the trigger. It carries no `auth_time`, so it cannot roll
itself into a session, and it lives an hour; a job that follows longer simply exchanges again.

## The request is durable, and the caller follows it

A sync clones the repository, reconciles the catalog and recomputes lineage: minutes, not milliseconds. Holding an HTTP
call open across that (as sync-now does, up to its wait) would tie the outcome to one connection and one replica, and a
restart would lose it. So a request is a row (`TriggerRequestStore`), answered 202 at once and moved along by
`TriggerRequestService` on every replica under a lease:

- **Exactly-once fires.** The lease is renewed right before a fire, and a schedule is marked firing before its runs are
  queued, so a replica that takes over a schedule found mid-fire looks for the runs that fire queued before it fires
  again. The run queue journals its own decisions, so the runs are there to find.
- **Idempotency belongs to the caller.** A re-run step must not fire twice, and only the caller knows whether two calls
  are the same intent; the request key (the action uses the run, attempt, job and step) is unique per caller.
- **The request keeps its own trace.** It copies the sync attempt's lines as they appear (a repository keeps only its last
  five sync traces) and writes each run's start and end exactly once, using a per-run conditional update rather than a
  watermark, because node clocks make end times unreliable for ordering.

## Run the merged commit, or a commit that contains it

A schedule fire pins its runs to the repository's last synced commit (`ScheduleFire` through the run queue), so a fire
issued straight after a merge would run the commit before it unless the sync had already landed. The request therefore
waits for an attempt that started after it asked. "Exactly that commit" is not always possible: the branch may move on
before the sync reads it. Containment is the honest rule (`CommitContainment`, a merge-base read from the history clone):
the synced commit contains the merge, or the request fails and says the commit is not on the branch the repository syncs.
A commit already synced needs no sync at all, and a merge-triggered sync skips the full lineage recompute a person's
sync-now forces, since the merge changed the estate anyway.

## The answer goes to the job, not back to GitHub

The job asked, so the job reports: the action streams the request's trace into the log (the sync's lines folded in a
group, failures raised as annotations), writes the server-rendered Markdown summary to the job's page, and keeps the
traces as files for an artifact. The summary is rendered by the control plane (`TriggerRequestSummary`) so a GitHub job,
another CI system and the GUI's copy button show the same text.

Posting a check run on the commit was considered and left out: it needs a GitHub App with write access (the estate's App
reads only), and it matters only for a fire longer than the six hours a hosted job may run. The request carries on
without its caller either way, and the GUI shows how it ends.

## What the old event webhook taught

SQLFlow v2 had an Azure Event Grid webhook (`EventTaskController`). Three of its choices are why this design differs:
its JWT travelled in the query string, so it sat in the event subscription's configuration and in access logs; its queue
was in memory, so events that arrived during a restart were lost; and it started one pipeline per file. Here the proof
travels in a body and is exchanged once, the request is a row, and one request fires each schedule once whatever it
carries.
