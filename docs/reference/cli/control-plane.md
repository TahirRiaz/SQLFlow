---
id: cli-control-plane
title: sqlflow control-plane verbs
type: cli-command
summary: Remote CLI verbs over the control plane's /api/v1 surface, covering what the GUI does from a terminal plus whoami, doctor, run history, and completions.
keywords:
  - control plane
  - login
  - trigger
  - runs
  - groups
  - schedules
  - repos
  - pipelines
  - datasources
  - search
  - lineage
  - whoami
  - doctor
  - completions
cliCommand: control-plane
related:
  - cli-run
  - cli-validate
  - cli-db
  - cli-worker
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.Estate.cs
  - src/SqlFlow.Cli/Remote/ControlPlaneClient.cs
---

# Control-plane verbs

The `sqlflow` binary drives the same `/api/v1` API the GUI uses, so anything verified in the browser can be
verified from a terminal, a shell script, or an LLM integration. The target resolves from `--url` or
`SQLFLOW_URL`; the credential from `--token`, then `SQLFLOW_TOKEN` (both suppliable through the git-ignored
`.sqlflow/env`), then the per-URL store `login` writes (`~/.sqlflow/credentials.json`, relocatable with
`SQLFLOW_CREDENTIALS_FILE`). Human output goes to stdout with notes on stderr; `--json` switches stdout to the
raw API shapes. Exit codes: 0 success, 1 error or a followed run that did not succeed, 130 on Ctrl+C.

## Signing in

```
sqlflow login --url https://sqlflow.example.com --username tahir     # password from a hidden prompt / piped stdin
sqlflow login --device                                               # RFC 8628 browser grant, for SSO accounts
sqlflow login --with-token                                           # paste an existing sqlf_ personal access token
sqlflow login --username ci-bot --no-store                           # print the minted secret once (CI bootstrap)
sqlflow logout                                                       # revoke the stored token and remove it
sqlflow whoami                                                       # subject, role, scopes, credential source
```

Password and device sign-ins mint a personal access token (`--token-name`, `--expires-days` default 90,
`--no-expiry`, `--scopes "read operate"` to narrow) and store that, never the password and never the
expiring session JWT, so the stored credential is always revocable server-side.

```
sqlflow user reset-password <username> [--db <conn-ref>]   # local-user password reset, direct against the catalog
```

`user reset-password` is a direct-catalog break-glass verb (it does not call the API): it resets a local user's
password in the catalog database addressed by `--db` (default `${env:SQLFLOW_CATALOG_DB}`), for recovering an
account when no one can sign in. It only affects local users; an SSO (Entra) user's credential lives in the
external identity provider, not the catalog. A `--db` value that embeds a literal credential warns on stderr.

## Execution

```
sqlflow trigger --repo bb --flow load_orders [--pool p] [--commit sha] [--full|--from d --to d|--file-pattern g]
sqlflow trigger --repo bb --flow load_orders --scope node --preview   # the flow + descendants, shown, not enqueued
sqlflow trigger --repo bb --scope batch --batch nightly --follow      # whole batch, live-streamed to the outcome
sqlflow runs list [--status failed --flow orders --latest]            # the run inbox
sqlflow runs show <runId> [--files --statements --assertions --keys --metrics]
sqlflow runs trace <runId> [--follow]                                 # consolidated trace; --follow rides the live SSE
sqlflow runs cancel <runId>                                           # API when a URL is configured; --db forces the
                                                                      # direct-catalog break-glass route
sqlflow groups show <groupId> [--follow] | cancel <groupId> | rerun <groupId>
```

## Estate

```
sqlflow summary                                    # the dashboard rollup
sqlflow nodes                                      # the worker fleet, heartbeat-derived liveness
sqlflow schedules list | show <id> | create --repo r --flow f (--cron "0 6 * * *"|--interval 3600)
                 [--timezone Europe/Oslo] [--disabled] [--catchup]
                 | pause <id> | resume <id> | delete <id>
sqlflow repos list | show <name> | sync <name>     # git source sync-now, or local-path re-sync
sqlflow repos register --name bb --remote-url https://... --credential-ref '${env:GIT_TOKEN}'
                 [--branch main] [--interval 3600] [--credential-user git] [--disabled]
sqlflow repos discover --remote-url https://...    # preview a remote's flows without importing
sqlflow pipelines list [--repo r --kind ing --active true] | show <id> [--yaml|--definition]
                 | columns <id> | files <id>
sqlflow search <term> [--objects|--columns|--definitions|--files|--flows]
```

## Datasources (remote compute)

The remote twins of `catalog` and `detect-unique-key`: operations queue as compute tasks and execute on a
worker node inside the network, so no direct database reachability is needed from this machine. The worker's
JSON result prints on stdout.

```
sqlflow datasources list
sqlflow datasources test --ref '${env:SQLFLOW_CONN_DW}'
sqlflow datasources objects --ref '${env:SQLFLOW_CONN_DW}' --schema dbo --like Ord
sqlflow datasources introspect --ref '${env:SQLFLOW_CONN_DW}' --object dbo.Orders
sqlflow datasources detect-unique-key --ref '${env:SQLFLOW_CONN_DW}' --object dbo.Orders
sqlflow datasources tasks | task <id> | cancel <id>
```

## Lineage as data

A console cannot draw the GUI's graph, but the dataset behind it prints, and with `--json` it is directly
consumable by an LLM or an integration test.

```
sqlflow lineage objects [--name Orders --kind view]
sqlflow lineage edges --repo bb [--object <key>] [--relation write]
sqlflow lineage waves --repo bb                    # the execution plan
sqlflow lineage script <key|flow>                  # the code behind any node (YAML or SQL)
```

The offline `sqlflow lineage <folder>` computation over a local flow estate is unchanged.

## Environment and testing helpers

```
sqlflow health                                     # anonymous /health/live + /health/ready probe; exit 0 when both 200
sqlflow doctor                                     # env file, SQLFLOW_* presence, control plane, credential, catalog
sqlflow validate <folder> [--json]                 # every document in the estate; exit 0 only when all parse (CI gate)
sqlflow runs local [folder] [--flow x] [--last N]  # the on-disk .sqlflow/runs history, no catalog needed
sqlflow completions bash|zsh|powershell            # shell completion script on stdout
```

`sqlflow health` needs no credential (both probes are anonymous): it reports the `/health/live` and `/health/ready` status codes and bodies, and exits 0 only when both answer 200. A failing `ready` under a passing `live` usually means the catalog database is unreachable from the control plane.
