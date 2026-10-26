---
id: guide-github-actions
title: "Syncing and firing schedules from GitHub after a merge"
type: guide
summary: Have a GitHub workflow sync a repository after a merge, fire the schedules it picks and show the sync and every run in the job, with no secret stored.
keywords:
  - github actions
  - workflow
  - merge
  - after merge
  - sync after merge
  - fire schedule from github
  - oidc
  - id-token
  - job summary
  - artifact
  - integrations/github
  - sync and fire action
  - workflow_dispatch
  - trigger key
related:
  - concept-external-triggers
  - concept-authentication-and-identity
  - flow-schedule
  - guide-github-app
sourceRefs:
  - integrations/github/action.yml
  - integrations/github/lib.mjs
  - gui/src/features/external-triggers/workflow.ts
  - gui/src/features/external-triggers/ConnectSheet.tsx
  - src/SqlFlow.ControlPlane/Api/ExternalTriggerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/TriggerRequestEndpoints.cs
  - src/SqlFlow.ControlPlane/Security/GitHubActionsTokenValidator.cs
---

# Syncing and firing schedules from GitHub after a merge

The control plane syncs each repository on its own interval. After a merge you usually want more: the repository synced
**now**, at the commit you merged, then a schedule (or a few) fired on it, and the job that merged to say what happened.
An [external trigger](../concepts/external-triggers.md) does that. The job proves who it is with the token GitHub
mints for it, so no secret is kept in GitHub or in SQLFlow, and SQLFlow's action follows the request and reports it in
the job:

- the job's log carries the request's trace as it happens: the sync's own lines (folded under **Repository sync**),
  each schedule's fire, and every run as it starts and ends, with failures raised as annotations;
- the job's page carries the summary: the commits, the sync, each schedule with every flow it ran, and the errors;
- an artifact carries the request's trace and every run's whole trace as files, when you keep it;
- the job fails unless the sync went through and every run succeeded.

## 1. Create the trigger

On **External triggers** (Admin), choose **New trigger**, keep **GitHub workflow** and give:

| Field | Give | Why |
| --- | --- | --- |
| Name | `estate-deliver` | What its runs record as the one that asked (`trigger:estate-deliver`). |
| Repository it syncs | the repo source | A request always syncs this repository and may fire only its schedules. |
| Schedules it may fire | the schedules a merge may fire, or **every schedule** | Each request picks among them; none makes a trigger that only syncs. |
| Repository | `owner/estate` | The GitHub repository whose workflows it trusts. |
| Branch or ref | `refs/heads/main` (the repo source's branch is the default) | A workflow on any other ref, a pull request's included, is refused. |
| Workflow file | `.github/workflows/sqlflow.yml` | Without it, every workflow of the repository is trusted, one added later included. |
| Environment | `production` (optional) | The environment's reviewers and branch rules then apply before a job gets in. |
| Repository id | after the first call (optional) | Pins the repository; **Last used** shows the id GitHub stated. |

The page warns about what it trusts too broadly and about a schedule the repository does not have yet (a merge may add
it). A deployment can declare the same trigger in its configuration instead
([configured triggers](../concepts/external-triggers.md#configured-triggers)).

## 2. Commit the workflow

The page then shows how to connect the trigger: pick the schedules **every merge** fires, and copy the workflow it
writes. Run by hand (**Run workflow** on GitHub), the workflow offers each of the trigger's schedules alone, the merge's
set, or none, as a choice. A workflow it writes looks like this:

```yaml
name: SQLFlow estate-deliver
on:
  push:
    branches: [main]
  workflow_dispatch:
    inputs:
      schedules:
        description: The schedules to fire after the sync
        type: choice
        default: nightly
        options:
          - nightly
          - hourly
          - none
permissions:
  id-token: write
  contents: read
concurrency:
  group: sqlflow-estate-deliver
  cancel-in-progress: false
jobs:
  sync:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4
      - uses: ./.github/actions/sqlflow
        with:
          control-plane: https://sqlflow.example.com
          audience: https://sqlflow.example.com
          trigger: estate-deliver
          schedules: ${{ github.event_name == 'workflow_dispatch' && (inputs.schedules == 'none' && ' ' || inputs.schedules) || 'nightly' }}
          traces: sqlflow-traces
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: sqlflow-estate-deliver
          path: sqlflow-traces
          if-no-files-found: ignore
```

`id-token: write` is what lets the job ask GitHub for its token. `concurrency` runs one merge's job after another's,
never beside it. The `audience` is the control plane's `ControlPlane:ExternalTriggers:GitHub:Audience`.

**The action.** It is SQLFlow's `integrations/github` folder: plain JavaScript with no dependencies, run on the runner's
own Node. By default the workflow uses a copy of it kept in the repository: copy the folder to `.github/actions/sqlflow`
(that is why the workflow checks the repository out first). An organization that keeps one copy for every repository
sets `ControlPlane:ExternalTriggers:GitHub:ActionReference` to it (for example `acme/sqlflow-actions/sync-and-fire@v1`),
and the page writes that instead.

## 3. What the action takes and gives

| Input | Default | Meaning |
| --- | --- | --- |
| `control-plane` | (required) | The control plane's address. Only `https` (or `http` to `localhost`). |
| `schedules` | none | The schedules to fire after the sync, separated by commas or new lines. |
| `commit` | `${{ github.sha }}` | The commit the repository must be synced at; the schedules fire only once the sync holds it. Ignored when `sync` is `false`. |
| `repo` | the trigger's own | The repository (repo source) to sync. |
| `sync` | `true` | `false` fires on the commit already synced. |
| `batch` | none | The `batch:` tags every fire is narrowed to. |
| `chain` | `true` | Whether each fire carries the schedules chained behind it. |
| `auth` | `github` | `github` (the job's token) or `key`. |
| `audience` | `sqlflow` | The audience to ask GitHub's token for. |
| `trigger` | none | The trigger to act as, when several trust the workflow. |
| `key` | none | With `auth: key`, the trigger's key, from a secret. |
| `wait` | `true` | `false` answers as soon as the request is made. |
| `timeout-minutes` | `120` | How long to follow the request before the step fails. |
| `poll-seconds` | `5` | How often to read the trace while nothing new happens. |
| `summary` | `true` | Whether to write the summary to the job's page. |
| `traces` | none | A folder to write the request's trace and every run's trace into. |
| `cancel-on-timeout` | `false` | Whether to cancel the request when the step stops following it. |
| `request-key` | the run, attempt, job and step | Asking again with the same key is answered with the request already made, so a retried step never fires twice; a re-run of the job is a new attempt, so it asks again. |

It sets the outputs `request-id`, `url` (the request in the GUI, when the control plane knows the GUI's address),
`state`, `conclusion` (`success`, `failure` or `cancelled`), `synced-commit` and `traces`. Its credential lives an hour;
the action exchanges again before it lapses, retries a control plane that answers 429 or 5xx or cannot be reached, and
masks every token in the log. When the job is cancelled, its post step cancels the request too.

## 4. Watch it

Merge to `main`. The job's log shows the request being made, the repository sync's lines, each fire and each run; the
job's page shows the summary; the request is also on **External triggers > Requests**, where a row opens its live trace
in the bottom panel, and the summary links there. Each run of the fire records `trigger:estate-deliver` as the one that
asked for it.

## From another system

A system that cannot present GitHub's token uses a **Key** trigger: the key is shown once when the trigger is created
(and again when it is given a new one); keep it in that system's secret store. The action takes it with `auth: key` and
`key: ${{ secrets.SQLFLOW_TRIGGER_KEY }}`; anything else makes two calls:

```bash
token=$(curl -sf -X POST "$CP/api/v1/auth/trigger/key" -H 'Content-Type: application/json' \
  -d "{\"key\":\"$SQLFLOW_TRIGGER_KEY\"}" | jq -r .accessToken)
curl -sf -X POST "$CP/api/v1/trigger-requests" -H "Authorization: Bearer $token" \
  -H 'Content-Type: application/json' -d '{"schedules":["nightly"]}'
```

and follows `GET /api/v1/trigger-requests/{id}` until its `conclusion` is set.

## When it does not work

| The job says | Because | Do |
| --- | --- | --- |
| `The job cannot get a GitHub token` | The job has no `id-token: write` permission. | Add `permissions: id-token: write` (and `contents: read`). |
| `The GitHub token is for audience '...'` | The action asked for another audience than the control plane checks. | Set the action's `audience` to `ControlPlane:ExternalTriggers:GitHub:Audience`. |
| `No enabled trigger trusts this job (...)` with each trigger's mismatch | The repository, its id, the ref, the workflow or the environment is not the one a trigger asks for. | Fix the workflow (its file name, its branch, its `environment:`) or the trigger, as the message names. |
| `Several triggers trust this job` | More than one trigger matches. | Set the action's `trigger`. |
| `Trigger '...' may not fire '...'. It may fire: ...` | The schedule is not among the trigger's. | Add it to the trigger, or fire one it lists. |
| `The sync of '...' failed: ...` | The repository could not be read or synced. | Read the sync's lines under **Repository sync**; fix the repo source's credential or the repository. |
| `The sync read ..., which does not contain ...` | The commit is not on the branch the repository syncs. | Run the workflow on that branch, or point the trigger's ref there. |
| `No schedule named '...'` | The merge does not define it, or renamed it. | Check the repository's schedules, which the message lists. |
| `... is paused by an operator` | Someone paused the schedule. | Resume it on the Schedules page. |
| `No sync of '...' answered the request within 30 minutes` | No control plane runs the managed sync, or the sync is stuck. | Check `ControlPlane:ManagedSync:Enabled` and the repository's sync trace. |
| `External triggers are turned off` | `ControlPlane:ExternalTriggers:Enabled` is false. | Turn it on. |
| `The request is still running after N minutes` | A schedule outlasted `timeout-minutes`. | Raise it; the request carries on either way, and its page shows how it ends. |

## See also

- [External triggers](../concepts/external-triggers.md)
- [Reading a private GitHub repository with a GitHub App](github-app.md)
