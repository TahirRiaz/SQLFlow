---
id: flow-scm
title: "Source-control flow (flowType: scm)"
type: flow-reference
summary: "flowType: scm scripts a SQL Server database's objects with SMO into a git working tree, commits the snapshot, and optionally pushes to a remote."
keywords:
  - source control
  - flowtype scm
  - smo
  - git snapshot
  - repository
  - scripting
  - schedule
  - schema history
  - ddl comparison
yamlPath: "(root, flowType: scm)"
related:
  - flow-overview
  - flow-schedule
  - concept-shadow-catalog
  - concept-lineage-graph-and-plan
sourceRefs:
  - src/SqlFlow.Yaml/YamlSourceControlFlowLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowDocumentHeaders.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Core/SourceControl/SourceControlFlow.cs
  - src/SqlFlow.Core/SourceControl/SourceControlObjectTypes.cs
  - src/SqlFlow.SourceControl/SourceControlService.cs
  - src/SqlFlow.SourceControl/SmoDatabaseScripter.cs
  - src/SqlFlow.SourceControl/SnapshotWriter.cs
  - src/SqlFlow.SourceControl/LibGit2GitWorkspace.cs
  - src/SqlFlow.ControlPlane/Api/GitHistoryEndpoints.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.Cli/Program.cs
---

# Source-control flow (flowType: scm)

A `flowType: scm` document scripts the full object definition of one SQL Server database to disk with SMO and commits the snapshot to a git repository, so the schema's change history lives in version control. Re-running the flow over time is what produces the diff history: an unchanged database re-scripts to byte-identical files (objects sorted, line endings normalized to LF), so only real changes surface as git diffs. The database is a declared connection resolved through the same secretless pipeline as every other flow; the git credential is a `${...}` reference, never a literal in the document.

This is the V3 port of the legacy `flw.SysSourceControl` / `flw.SysSourceControlType` pair and its `ExecSourceControl` runner: one document replaces one `SysSourceControl` row, and the git credentials that lived in `SysSourceControlType` are secret references instead of catalog columns.

A snapshot is a maintenance flow: it runs ON the estate rather than THROUGH it. That shapes two behaviors worth knowing before authoring one:

- It is a full pipeline. It registers in the shadow catalog with kind `scm`, carries a `schedule:`, is fired by the control plane's scheduler onto the normal run queue, and keeps run history like any other flow. Nothing extra needs to be installed to run snapshots unattended.
- It is not part of lineage. It reads object DEFINITIONS and writes a git tree, so it moves no data between catalog objects. It contributes no lineage node, no edge, and no dependency, never joins an execution wave, and never pulls a batch into a false ordering. See [Catalog, scheduling, and lineage](#catalog-scheduling-and-lineage).

## Minimal example

```yaml
flowType: scm
name: local-snapshot
connections:
  DW:
source:
  server: DW
repository:
  path: ./snapshot
```

This scripts the connection's default catalog into a local git repository at `./snapshot` (initialized on first run) and commits on branch `main`. No remote, no credential, history stays local.

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | | Must be `scm` (case-insensitive, trimmed) to select this loader. |
| `name` | string | yes | | The flow name (legacy SysAlias): the snapshot's identity, the run-history folder, and the seed for the stable flow id. |
| `description` | string | no | null | Free-text description. |
| `batch` | string | no | null | Grouping label (the legacy `SysSourceControl.Batch`). A filter only: no ordering, no effect on scheduling. |
| `schedule` | map/string/list | no | none | The document-envelope schedule, identical to every other flow kind. This is how a snapshot runs unattended. |
| `connections` | map | no | | Document-local named connections; a bare alias (`DW:`) resolves the conventional `${env:SQLFLOW_CONN_DW}`. |
| `source` | map | yes | | The database to script. Exactly one of `server` or `connection`, plus optional `provider` and `database`. |
| `source.server` | string | one of server/connection | | Name of a connection declared under `connections:`. |
| `source.connection` | string | one of server/connection | | An inline connection string or `${...}` reference; synthesizes a connection named `source`. |
| `source.provider` | string | no | mssql | Provider of an inline `source.connection`. The resolved connection must be SQL Server (`mssql` or `azdb`). |
| `source.database` | string | no | null | Explicit database to script; null means the connection's default catalog. |
| `repository` | map | yes | | Where and how the snapshot is committed. At least `repository.path`. |
| `repository.path` | string | yes | | The local git working directory. Relative paths resolve against the document's directory. |
| `repository.remote` | string | no | null | HTTPS git remote URL to clone from and push to; null keeps history purely local. |
| `repository.branch` | string | no | `main` | The branch snapshot commits land on. Created if missing or if the repository is empty. |
| `repository.username` | string | no | null | A whole `${...}` reference to the git username. Never a literal. |
| `repository.secret` | string | no | null | A whole `${...}` reference to the git secret (BitBucket app password or GitHub token). Required when `remote` is set. Never a literal. |
| `repository.author.name` | string | no | `SQLFlow` | Commit author/committer display name. |
| `repository.author.email` | string | no | `sqlflow@localhost` | Commit author/committer email. |
| `scripting` | map | no | all types, schema-only, staging schema skipped | What the scripter captures. |
| `scripting.data` | list of string | no | `[]` | Tables whose row data is scripted as INSERTs, in addition to their schema. |
| `scripting.include` | list of string | no | `[]` | If non-empty, only these object categories are scripted (allowlist). |
| `scripting.exclude` | list of string | no | `[]` | Object categories to skip, applied after `include`. |
| `scripting.excludeSchemas` | list of string | no | `[raw]` | Schemas skipped whole, the schema itself and every object in it. Defaults to the engine's staging schema. |
| `scripting.parallelism` | integer | no | `8` | How many connections script objects concurrently, 1 to 32. Changes wall-clock only, never the snapshot. |

Unmatched YAML keys are ignored (the loader is built with `IgnoreUnmatchedProperties`), so a typo in an optional key silently drops it; `sqlflow validate` confirms what actually parsed.

## name

Required. Omitting it fails with `'name' is required for a source-control flow (flowType: scm).` The name becomes `SysAlias` (the run-history folder under `.sqlflow/runs/<name>/`) and deterministically derives the positive integer flow id, so logs and artifacts key consistently across runs without any control database.

## source

Required (`'source' is required.`). It follows the same endpoint-connection convention as every other flow:

- `source.server: <name>` references a connection declared under `connections:`. An undeclared name fails: `'source.server' references '<name>', which is not declared under 'connections:'.`
- `source.connection: <ref>` supplies the connection inline (a connection string or a `${env:...}`/`${keyvault:...}` reference) and synthesizes a connection named `source`. If `connections:` already declares `source`, that is an error.
- Setting both fails: `'source' sets both 'server' and 'connection'; use exactly one.` Setting neither fails with a message telling you to set one of them.

SMO scripts SQL Server, so a foreign-provider source is rejected at parse time: `the source connection '<name>' is '<kind>'; a source-control flow's source must be SQL Server (mssql or azdb).`

### source.database

Optional. When null, the scripter uses the connection's default catalog: the connection string's `Initial Catalog` when present, otherwise the server connection's database name. If neither yields a database, the run fails with `The source-control connection has no default database; set 'source.database' to the database to script.` A database the login cannot reach fails with `Database '<name>' was not found on the server, or the login cannot access it.`

Scripted objects land under a folder named for the resolved database, so one repository can hold snapshots of several databases side by side.

## repository

Required. Omitting the block fails with `'repository' is required (at least 'repository.path').`

### repository.path

Required (`'repository.path' is required (the local git working directory).`). A relative path is resolved against the directory containing the flow file (paths that are rooted or contain `://` are left as-is). On first use the directory is created and either cloned from `repository.remote` or initialized as a fresh local repository.

### repository.remote and repository.secret

`remote` is the HTTPS URL of a BitBucket or GitHub repository. When set, the working directory is cloned from it on first use and the snapshot commit is pushed to it. Setting a remote without a secret fails at parse time:

```text
'repository.remote' is set but 'repository.secret' is not. Pushing needs a credential; set 'repository.secret' to a ${env:...} reference (a BitBucket app password or a GitHub token), or remove 'repository.remote' to keep the history local.
```

Both `repository.secret` and `repository.username` must be whole `${...}` references (the value starts with `${` and ends with `}`). A literal value fails:

```text
'repository.secret' must be a ${env:NAME} or ${keyvault:vault/secret} reference, never a literal. Put the value in the git-ignored .sqlflow/env file or your secret store.
```

The references are resolved at run time, and only when a remote is set; local-only history resolves no credential at all. For GitHub, any non-empty username works next to a personal access token (when `username` is omitted the placeholder `x-access-token` is used); BitBucket needs the account username plus an app password.

### repository.branch and repository.author

`branch` defaults to `main`. Before any file is written the workspace lands on that branch: an existing branch is checked out, a missing branch is created from HEAD, and in an empty repository the unborn HEAD is pointed at it so the first commit creates it. The push uses an explicit `refs/heads/<branch>:refs/heads/<branch>` refspec with no leading `+`, so a non-fast-forward push is rejected by the server instead of rewriting history.

`author.name` defaults to `SQLFlow` and `author.email` to `sqlflow@localhost`; both are used as the commit author and committer.

## scripting

Optional. With no `scripting` block every supported object category is scripted schema-only, except the engine's staging schema (see [scripting.excludeSchemas](#scriptingexcludeschemas)).

### scripting.include and scripting.exclude

Each entry must be one of the 19 object categories in `SourceControlObjectTypes.All`, compared case-insensitively:

```text
Schema, UserDefinedDataType, UserDefinedType, XmlSchemaCollection, Sequence, PartitionFunction, PartitionScheme, Table, View, StoredProcedure, UserDefinedFunction, UserDefinedAggregate, UserDefinedTableType, Synonym, Rule, Default, DatabaseDdlTrigger, FullTextCatalog, SecurityPolicy
```

An unknown entry fails at parse time: `'scripting.include' has unknown object type '<value>'. Allowed: <the list above>.` (Note the plural `Tables` is not a valid entry; the categories are singular.)

`include` is an allowlist: when non-empty, only those categories are scripted. `exclude` is applied after `include` and removes categories. Blank entries in either list are skipped.

### scripting.excludeSchemas

Schemas the snapshot skips whole: the `CREATE SCHEMA` script itself, and every table, view, procedure, function, or other schema-qualified object in it. Names are compared case-insensitively, and each entry is trimmed and unbracketed (`[work]` is the same schema as `work`), blanks dropped, duplicates removed.

It defaults to `raw`, the engine's own staging schema (`StagingConventions.SchemaName`, src/SqlFlow.Core/Ingestion/StagingConventions.cs). That schema holds each flow's canonical staging and match-key work tables (`[raw].[<targetSchema>_<targetTable>_<flowId>]`), which every run rebuilds and drops on success, so they are transient by construction and belong to no database's tracked definition. Versioning them would fill every snapshot with churn over objects nobody reviews, and a work table dropped between SMO enumerating it and scripting it fails the run outright with `Invalid object name 'raw.<table>'`.

An authored list REPLACES the default rather than adding to it, so a flow that excludes its own scratch schema and still wants staging skipped must name both:

```yaml
scripting:
  excludeSchemas:
    - raw
    - scratch
```

An explicitly empty list scripts every schema, including staging:

```yaml
scripting:
  excludeSchemas: []
```

Excluding a schema also excludes it from `scripting.data`: naming one of its tables there is skipped with the warning `scripting.data names '<name>', which is in the excluded schema '<schema>'; skipped.` rather than versioning the rows of a table whose definition the same run refused to script.

Two things this filter does not touch: system schemas (`dbo`, `sys`, `INFORMATION_SCHEMA`, `guest`, and the fixed database roles) are excluded from the `Schema` category regardless, and objects that belong to no schema at all (a database DDL trigger) are never excluded this way.

### scripting.parallelism

How many connections script objects at the same time, between 1 and 32 (an authored value outside that range fails at parse time with `'scripting.parallelism' must be between 1 and 32, but was <value>.`). It defaults to 8.

Scripting one object with full DRI, indexes, triggers, and extended properties costs SMO dozens of small round trips, so a snapshot's cost is round-trip latency, not server work: against a remote instance a single table takes roughly a second of wall-clock while the server is idle. Walked on one connection, a few hundred objects therefore take many minutes. Each lane is an independent connection with its own SMO `Server` and `Scripter` (SMO objects are not thread-safe, but separate instances on separate connections are), enumeration stays on the single lead connection, and each lane scripts by `Urn`, which resolves against whichever server it is handed. The emitted SQL and the resulting files are byte-identical to a single-lane walk; only the wall-clock and the connection count on the scripted server change.

Lower it when the scripted server is connection-constrained, or set it to `1` to walk the database on the lead connection alone:

```yaml
scripting:
  parallelism: 1
```

A category smaller than the lane count uses only as many lanes as it has objects, and a lane is opened on the first object it is handed, so a narrow `scripting.include` never opens connections it does not use.

### scripting.data

Tables whose row data is scripted as INSERT statements in addition to their schema (the legacy `ScriptDataForTables`), intended for reference and seed tables worth versioning. Each entry is normalized to a canonical `schema.table`:

- The name is split on dots outside brackets, so `dbo.Config`, `[ref].[Calendar]`, and `MyDb.dbo.Config` all parse; a single surrounding bracket pair is stripped from each part.
- The rightmost two parts are kept; a one-part name gets schema `dbo`.
- Entries are de-duplicated case-insensitively. An entry that is only dots or brackets fails: `'scripting.data' has an empty table name.`

At run time, tables with an identity column have their INSERTs wrapped in `SET IDENTITY_INSERT ... ON/OFF` so the data restores faithfully, and the emitted INSERTs are sorted for a deterministic snapshot. A data entry that names something that is not a user table produces a warning (`scripting.data names '<name>', which is not a user table in <database>; skipped.`) rather than a failure.

## Repository layout

One folder per object type, one file per object, under a folder named for the resolved database (the legacy `SmoHelper.ScriptFolders` layout):

```text
<database>/<category>/<schema>.<name>.sql
```

Objects without a schema (for example a database DDL trigger) use just `<name>.sql`. Row data from `scripting.data` lands under the distinct `Data` folder, kept separate from the schema-only `Table` folder so a data snapshot never collides with the table definition. System objects and system schemas are excluded, as are the schemas named by `scripting.excludeSchemas` (the engine's staging schema by default).

Schema scripts carry full DRI, indexes, triggers, full-text indexes, and extended properties, with no drops, permissions, owners, or statistics, and no headers, so diffs stay clean. Each SMO batch is terminated with `GO`.

## Run pipeline

`SourceControlService.RunAsync` in src/SqlFlow.SourceControl/SourceControlService.cs is the single code path for the CLI and the tests:

1. Resolve the source connection (`ConnectionRole.Source`, addressed as `@<server>`).
2. Script every selected object with SMO (sequentially, one server connection).
3. Prepare the git workspace (clone or init, ensure the remote and branch) via LibGit2Sharp; no external git binary is needed.
4. Write the snapshot into the working tree, recording each file as Added, Changed, Unchanged (byte-identical, skipped), or Deleted (its object no longer exists, so the file is removed and the next commit records the deletion). A scripted object may never write outside the working tree; a hostile object name with traversal sequences is refused.
5. Commit and push. Only the database's own subtree is staged (the git workspace is scoped by `PathScope` to the database folder), so several scm flows can share one repository without cross-deleting each other's folders. When nothing in the subtree changed, no commit is made (`Committed=false`). The commit message is `<flow name>: snapshot <database> (N added, N changed, N deleted)`.

Operational failures return a failed `SourceControlResult` (`Success=false`, with the error message redacted of secrets) rather than throwing, so the run still writes its artifact.

The result records `RunId`, `DatabaseName`, `WorkingDirectory`, `Remote`, `Branch`, `DryRun`, `ObjectsScripted`, the `Added`/`Changed`/`Deleted`/`Unchanged` counts, `Committed`, `CommitSha`, `Pushed`, every scripted object's relative path, scripter warnings (an individual object that fails to script becomes a warning, not a failure: the commonest cause is an object enumerated and then dropped by whoever owns it, which SMO reports as `Invalid object name`), and `DurationSeconds`. The one exception is a lost connection, which would otherwise turn every remaining object into a warning and commit the whole database as deleted: the server connection is checked whenever an object fails, and a closed one abandons the snapshot instead.

## Catalog, scheduling, and lineage

A source-control document is projected by the same header projection every other flow kind goes through (`FlowDocumentHeaders.Project`, src/SqlFlow.Lineage/Collection/FlowDocumentHeaders.cs), so it lands in the shadow catalog as a `CatalogPipeline` row with `Kind = "scm"`, its `Batch`, its `RelativePath`, its `Lifecycle`, the scripted server as `SourceServer`, and `file://` as `TargetServer` (a snapshot reads a database and writes a git tree). It is listed, filtered, and triggered like any other pipeline, in the GUI and from the CLI.

### Scheduling

The `schedule:` block is the document envelope's, parsed before the flowType dispatch, so it takes every form the [schedule reference](schedule.md) documents: an inline cron or interval, a named block other flows can join, or a reference joining an existing named schedule. The catalog sync mirrors it, and the control plane's `SchedulerService` fires it onto the same durable run queue a manual trigger uses. No separate service, timer, or agent runs snapshots; there is one scheduler and one run path.

```yaml
flowType: scm
name: warehouse-scm
batch: schema-history
connections:
  DW:
source:
  server: DW
repository:
  path: /var/sqlflow/scm/warehouse
  remote: ${env:SCM_REMOTE}
  username: ${env:SCM_USER}
  secret: ${env:SCM_TOKEN}
schedule:
  cron: "0 3 * * *"
  timezone: Europe/Oslo
```

That flow snapshots the warehouse every night at 03:00 Oslo time and commits only what actually changed.

### Lineage

A source-control flow is deliberately excluded from the lineage graph. The projected header carries `ParticipatesInLineage = false`, and `LineageGraphBuilder` drops such flows at a single gate before any node is built (src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs), so the exclusion is total and cannot be reached around:

- no `LineageFlowNode` in `lineage.json`, so nothing shows in the lineage explorer or the graph dumps;
- no edges and no flow dependencies, in the declared tier or the observed one (its run artifacts are recognized, so they raise no orphan warning, but contribute nothing);
- no execution wave, so it can never delay or reorder a data flow, and its catalog `Wave` stays at the `-1` "not computed" sentinel;
- no batch membership, since batches are computed FROM lineage.

This is intentional and not a limitation: a snapshot reads `sys` catalog metadata to script definitions, which is not a data dependency. Treating it as one would order the estate around a flow that moves no data.

Being out of the graph does not weaken anything else: the flow still has a lifecycle, run history, notifications on failure, and a schedule.

### Schema history in the catalog and the GUI

Every difference a snapshot finds is also recorded in the shadow catalog, one row per object, so "what changed in
the warehouse this week" is a query rather than a diff of commits nobody has cloned. The rows carry the database,
the object category, its schema and name, whether it was added, changed, or dropped, the commit that holds the
diff, and when the snapshot observed it. A run that finds nothing writes no rows.

Three MCP tools read it, kept deliberately distinct from the pipeline-history tools so an assistant picks the
right one: `database_schema_changes` searches what changed in a managed DATABASE, `database_object_compare`
returns one object's whole script at both ends of a window (the net change), and `database_object_ddl` returns
the DDL patch for one object at one snapshot. The pipeline YAML's own history is `flow_definition_history`, a
different question about a different artifact.

The GUI reads this at **Explore > Schema changes**: a database, schema, object tree over a date window (last 24
hours by default, which on a daily snapshot cadence is the last run), where selecting an object shows every time a snapshot saw it move, each linking to the run.
The `/api/v1/schema-changes` endpoint serves the same data, with `database`, `changeType`, `since`, and `search`
filters, and `/api/v1/schema-changes/databases` returns the per-database tally.

Below the tree, the selected object's script is shown at both ends of the window, side by side (or as one inline
patch), which is the "what actually changed" behind a row. The comparison is served by
`/api/v1/schema-changes/{id}/compare?since=<iso>`:

- The **after** side is the branch tip, the state the last snapshot left.
- The **before** side is the newest commit at or before `since`, the state the window opened on. Omitting
  `since` (an all-time window) leaves no earlier side, and the whole script reads as added, which is what
  "since this estate began snapshotting" means.
- However many snapshots touched the object inside the window, the answer stays ONE before and ONE after. A
  table four nightly runs edited reads as one net change, not four patches to reconcile by hand.
- An object added inside the window has no before text (its file was not in the base commit); one dropped
  inside it has no after text, and its last known script stays on the before side so the drop is reviewable.
- Either side is clipped at 200,000 characters and reported clipped, so a `scripting.data` snapshot of a large
  reference table cannot turn one comparison into a multi-megabyte response.

The row's id carries the object's identity, so no repository path is passed from the browser: the path is
rebuilt server-side from the same `<database>/<category>/<schema>.<name>.sql` convention the snapshot writer
emits, and the git credential is resolved in the control plane from the scm flow's stored `${...}` reference.
A client never holds a token, and cannot address a file the schema history does not know about. A comparison
needs `repository.remote` to be set: a local-only snapshot lives on the node that ran it and cannot be read
back, which the page says rather than showing an empty diff.

Two properties are worth knowing before reading the dates as gospel:

- A change is dated to the snapshot that first SAW it, not to when the DDL ran. On a daily cadence that means the
  day, not the minute.
- A `--dry-run` records nothing. It writes a working tree nobody keeps, so dating a change to a rehearsal (and
  then reporting it again on the next real run) would be worse than silence.

The first snapshot of a database that was never tracked records every object it finds as `Added`. That is
accurate rather than noisy, but it does mean the window right after onboarding a database is dominated by it.

### Running several databases

One document snapshots one database. For an estate of databases, author one document per database, all pointing at the same `repository.path` and remote: each writes only its own `<database>/` subtree and each commit stages only that subtree, so they never cross-delete each other's folders. Give them a shared `batch:` to list them together, and have them join one named schedule so a single fire covers the estate.

### Watching a run

A snapshot walks every object in a database and then pushes a repository, so a large database takes minutes. It
narrates itself onto the run's canonical event stream while it works, which is what the GUI's live trace panel
tails and what `run.json` persists:

```
connect  Resolving the connection for 'dwh'.
script   Scripting every object category from dw-dwh-prod.
script   Schema: enumerating.
script   Schema: scripting 10 object(s).
script   Schema: 10 of 10 scripted.
script   Table: enumerating.
script   Table: scripting 1,204 object(s).
script   Table: 63 of 1,204 scripted.
...
script   Scripted 1,565 object(s) from dw-dwh-prod.
git      Preparing https://bitbucket.org/... [main]: clone or fetch, then reset onto the remote.
write    Writing the snapshot into the working tree.
write    128 added, 296 changed, 442 deleted, 699 unchanged.
commit   Committing 866 change(s).
commit   Committed ebb7d85a and pushed to https://bitbucket.org/...
done     Snapshot of dw-dwh-prod finished in 168.4s: 1,565 object(s) scripted, 128 added, 296 changed, 442 deleted, committed and pushed.
```

Progress is reported per object category and paced by the clock rather than by a round object count: running
tallies go out at most once a second, so a database with thousands of objects stays readable while the trace never
goes silent long enough to look hung, whatever the scripting rate happens to be. The lines that mark a boundary
are never throttled, since a dropped one would leave a silent gap in front of it: each category is announced
before it is enumerated (`Table: enumerating.`, a call that on a large collection blocks for many seconds) and
again once its size is known, and each category's closing tally is forced so it always ends complete. A forced
tally that would only repeat the previous line is dropped rather than printed twice. An object that cannot be
scripted becomes a warning event and the run continues, and warnings are never throttled. A
quiet day says so explicitly (`Nothing changed since the last snapshot, so there is nothing to commit`) rather
than going silent, because no commit is the expected outcome once an estate settles, not a failure.

`trace.sql` stays empty for an scm run, deliberately: the generated DDL IS the artifact and lands in the
repository, so mirroring thousands of CREATE statements into the trace would bury the signal rather than add any.

## CLI

An scm document runs through the same `validate`/`run` commands as every other flow document; two run flags are scm-specific:

- `--dry-run` scripts and writes the working tree without committing (`Committed=false` in the result); inspect the pending diff with plain `git status`/`git diff` in the working directory.
- `--no-push` commits locally but does not push to the remote.

```bash
sqlflow validate warehouse-scm.yaml
sqlflow run warehouse-scm.yaml --dry-run
sqlflow run warehouse-scm.yaml --no-push
sqlflow run warehouse-scm.yaml --json
```

The run writes its artifacts to a timestamped folder under `.sqlflow/runs/<name>/` next to the flow file: `run.json` (the uniform result envelope), `run.log`, `scm.json` (the full `SourceControlResult`), and an empty `trace.sql` (an scm run generates no SQL trace). The most recent 50 run folders are kept per flow; older ones are pruned.

## Full example

```yaml
flowType: scm
name: warehouse-scm
description: nightly schema snapshot
batch: schema-history
connections:
  DW:
source:
  server: DW
  database: Warehouse
repository:
  path: ./scm/warehouse
  remote: ${env:SCM_REMOTE}
  branch: release
  username: ${env:SCM_USER}
  secret: ${env:SCM_TOKEN}
  author:
    name: SQLFlow Bot
    email: bot@sqlflow.io
scripting:
  data:
    - dbo.Config
    - "[ref].[Calendar]"
  exclude:
    - SecurityPolicy
schedule:
  cron: "0 3 * * *"
  timezone: Europe/Oslo
```

The bare `DW:` connection resolves `${env:SQLFLOW_CONN_DW}`; `SCM_REMOTE`, `SCM_USER`, and `SCM_TOKEN` live in the process environment or the git-ignored `.sqlflow/env` file next to the document. Every object category except `SecurityPolicy` is scripted schema-only into `./scm/warehouse/Warehouse/...`, the rows of `dbo.Config` and `ref.Calendar` land under `Warehouse/Data/`, and each snapshot commits to the `release` branch and pushes to the remote.

## See also

- [Anatomy of a flow file](overview.md) for the flowType dispatch and what every document kind shares.
- [Connections](connections.md) for the `connections:` block, `${...}` references, and the conventional environment variables.
