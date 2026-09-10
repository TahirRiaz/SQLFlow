---
id: wiki-recipe-inspect-and-debug
title: "Recipe: seeing what a flow will do, and finding out why it did not"
type: narrative
summary: "The read-only commands that answer a question without running anything, and the ordered checklist when a flow loads nothing."
keywords:
  - debug
  - plan
  - validate
  - discover
  - paths
  - flatten
  - dry-run
  - run artifacts
  - troubleshooting
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Ingestion/SqlTrace.cs
referenceRefs:
  - cli-validate
  - cli-plan
  - cli-discover
  - cli-paths
  - cli-flatten
  - concept-run-artifacts
related:
  - wiki-chaining-flows-through-the-lake
  - wiki-ignored-yaml-keys
  - wiki-recipe-backfill-and-replay
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: seeing what a flow will do, and finding out why it did not

Everything here is read-only except the last section. Reach for these before running anything
against a real target.

## The read-only ladder

```bash
# 1. does it parse, and does it leak a credential?
sqlflow validate vendor/vendor_trips_01_jsn.yaml

# 2. what DDL would it run? (reads source + live target, executes nothing)
sqlflow plan vendor/vendor_trips_01_jsn.yaml

# 3. what is actually IN the source files?
sqlflow discover vendor/vendor_trips_01_jsn.yaml

# 4. every addressable path in a file or folder, and the column each becomes
sqlflow paths "https://account.dfs.core.windows.net/fs/raw/vendor/api/trips/history/" -r

# 5. see the flattened rows themselves
sqlflow flatten "./sample.json" --data --max-records 20

# 6. generate a runnable flow stub from a file
sqlflow flatten "./sample.json" -o vendor/vendor_trips_01_jsn.yaml
```

`plan` is the one to build the habit around: it diffs the source schema against the live target and
prints exactly what would change, without changing it.

## "It ran, it succeeded, it loaded nothing"

Work down this list. Each step is one command.

```bash
# a. is the flow even reading the path you think?
sqlflow validate vendor/vendor_trips_01_jsn.yaml -v

# b. does the glob match anything?
sqlflow paths "https://account.dfs.core.windows.net/fs/raw/vendor/api/trips/history/" -r --pattern "vendor_trips_*.json"

# c. is the producer writing where the consumer reads?
sqlflow lineage vendor/ --json -o /tmp/l.json
grep -o 'az://[^"]*vendor[^"]*' /tmp/l.json | sort -u
```

The usual causes, in the order they actually occur:

1. **`searchSubDirectories` is off** and the producer partitions by `{yyyy}/{MM}`. The consumer sees
   only the top folder.
2. **`location` includes a dynamic segment.** It must be `landing.target` plus only the STATIC
   prefix of `pathTemplate`; everything from the first `{token}` belongs in `srcFile`.
3. **A watermark is already past the data.** See
   [recipe-backfill-and-replay](recipe-backfill-and-replay.md).
4. **`includePaths` is too narrow.** A `jsonPath` under a non-whitelisted ancestor lands nothing, and
   reads as an empty column rather than an error.
5. **`rootPath` points at the wrong node,** so the record array is never found.

## "The chain is broken but each flow works"

```bash
sqlflow lineage vendor/ --strict
sqlflow lineage vendor/ --explain vendor_trips_02_ing
sqlflow lineage vendor/ --of arc.Vendor_Trips --up
```

If a flow that should be in wave 1 or 2 sits in wave 0, nothing bound it. Check, in order:

- the lake path on both sides, after normalization (the `viaObjects` key in the JSON);
- the pre view name, which is `v_` + the file flow's `target.table`, and is what the `ing` flow's
  `source.object` must name;
- whether the flow is an `sp`, which needs `--connect` to get its edges from the derived tier and
  otherwise appears as an unconnected node.

## "The setting I added did nothing"

Assume the key never bound before assuming it had no effect. Loaders ignore unmatched properties, so
a misspelled or misplaced key is dropped silently.

```bash
# is this key real for this flow kind?
grep -o '"path": "[^"]*retry[^"]*"' docs/reference/flow/keys.api.json
```

Two live examples are written up in [ignored-yaml-keys](../incidents/ignored-yaml-keys.md):
`retry.backoffSeconds`, which does not exist, and `truncateBeforeLoad` placed under `load:` instead
of `target:`.

Note the census can itself be behind the engine, so a key absent from it is either invalid or a
census gap. See [census-drift](../maps/census-drift.md).

## "The types are wrong"

```bash
# what would inference choose, and why?
sqlflow infer vendor/vendor_trips.infer.yaml
```

Landing is string-first, so a wrong type is a view rewrite plus a replay, never a re-download. Pin
a stubborn column rather than fighting the inference:

```yaml
schema:
  overrides:
    metadata: { type: "nvarchar(max)" }
```

## After the run: the artifacts

Every run writes `run.json` under `.sqlflow/runs/<flow>/`, carrying the events, the row counts, the
watermark before and after, and the SQL trace.

```bash
sqlflow run vendor/vendor_trips_01_jsn.yaml --json --show-sql --log-level debug

# what did the last run actually do?
cat .sqlflow/runs/vendor_trips_01_jsn/*/run.json | head -60
```

Read `watermarkAfter` when a run reports success with zero rows: it usually explains the whole thing.

Remember these are node-local. A `response`-source acquisition watermark lives here and does not
survive a redeploy or a change to the flow's name, which is what
[api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md) is about.

## Projecting into the catalog

```bash
sqlflow db status                 # migration drift
sqlflow db migrate                # apply pending migrations
sqlflow db sync vendor/           # project the YAML estate, run history and lineage
sqlflow db sync vendor/ --connect # include derived-tier lineage, so sp flows connect
```

Running a flow from a folder registers that folder as a repository in the catalog, so a local run
can make a flow appear twice under a folder-named repo. Clearing it is a `db sync` against an empty
directory.
