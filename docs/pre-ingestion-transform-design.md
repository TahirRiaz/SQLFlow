# Pre-ingestion transform view (design)

> Pre-implementation design document; it has not been re-verified against the current codebase.
> For the current, code-verified behavior see
> [docs/reference/concepts/pre-ingestion-transform.md](reference/concepts/pre-ingestion-transform.md)
> and [docs/reference/flow/transform.md](reference/flow/transform.md).

Restores the SQLFlow V2 `flw.PreIngestionTransform` capability in V3: authored per-column transforms
and inferred data types, projected into a typed transformation view generated as a post-process of the
landing flow, with the transform metadata persisted centrally so the estate is queryable for "which
transformations are set or detected on a pipeline". YAML is the source of truth.

## Locked decisions

- Scope: YAML metadata + runtime view generation + central persistence + GUI + extensive tests.
- Placeholder token in expressions: `@ColName` (V2 parity), substituted with the quoted source column.
- Catalog captures both `declared` (from YAML) and `detected` (from a run's inference report).
- Topology (the V2 chain, confirmed by the author):
  - Files:        `file -> [pre] table -> transform view -> raw table -> target table`
  - Native SQL:   `table -> raw table -> target table` (already typed at source; no pre/view)
  - External DBs: `table -> [pre] table -> transform view -> raw table -> target table` (foreign types
    are unreliable, so external sources get the same infer+view treatment as files)
  - `pre` is a dedicated staging DATABASE holding temp, deletable data. The landing flow's YAML target
    connection points at it; no transform knob selects it (no `stagingDatabase` setting).
  - `raw` is a staging SCHEMA inside the prod/target database, persistent, used for incremental updates.
  - COMPOSITION IS CHAINED FLOWS (V2-style): the landing flow (file, or external-DB ingestion) ends at
    the pre table + transform view; a separate downstream ingestion flow reads THE VIEW as its source
    into the raw/target tables through the existing staging+upsert machinery. A view is a readable
    source object, so the typed hop needs no new load path (single code path holds).
  - The pre table and the view persist and are re-synced/refreshed on every execution
    (`CREATE OR ALTER VIEW`), which is what makes dynamic schema evolution flow
    pre -> view -> raw -> target.
- View generation is a POST-PROCESS of the landing flow's load, ON by default
  (`transform.generateView: true`); `false` disables it. It runs when
  `GenerateView && (Inference.Enabled || Inference.Columns.Count > 0)`; otherwise nothing is generated.
- View name: `v<Table>` in the same database + schema as the flow's target table.

## YAML shape

```yaml
transform:
  inferTypes: true            # detect types for columns no authored transform names
  generateView: true          # default; false = skip the view post-process
  onConvertError: fail
  columns:
    - name: vehicle_type      # source column (virtual: the computed column's own name)
      expr: "CAST(@ColName AS varchar(50))"
      as: vehicle_type_clean  # output alias
      type: "varchar(50)"     # declared type; with no expr -> CAST(@ColName AS type)
      order: 10               # position in the view
      virtual: false          # computed column with no source counterpart (needs expr; no @ColName)
      excludeFromView: false  # drop from the view's final projection
```

## Layers

1. Model (done): `ColumnTransform`, `TypeInferencePolicy.Columns` + `GenerateView`
   (`src/SqlFlow.Core/Model/TypeInference.cs`).
2. YAML (done): `TransformYaml.columns`/`generateView`, validation in `YamlFlowLoader.MapTransformColumns`,
   `schemas/sqlflow.flow.schema.json`.
3. Engine (done): `ColumnTransformExpression` (@ColName), `ColumnTransformResolver` (declared+inferred+
   passthrough merge, ordering, exclude/virtual), `TransformViewBuilder` (CREATE OR ALTER VIEW).
4. Catalog (done): `CatalogPipelineColumn` entity + migration `PipelineTransformColumns`;
   `CatalogProjection.PipelineColumnsDeclared`/`PipelineColumnsDetected`; declared wired into
   `CatalogSync` full sync + per-run write-back (replace by (pipeline, kind)).
5. Runtime (in progress): the view post-process in FlowRunner (file flows), then IngestionFlowRunner
   (external-DB landings; needs the transform policy on the relational `IngestionFlow` model + loader).
6. Detected persistence: the run result carries the resolved view columns; run.json serializes them;
   the catalog write-back and full sync project them as `detected` rows (replace by (pipeline, detected)).
7. API + GUI: `/pipelines/{id}/columns` endpoint + a Transforms tab.
8. Tests: unit (done for 1-4), runtime integration, catalog projection, API, GUI.

## Runtime post-process (FlowRunner, then IngestionFlowRunner)

After the load (and the flow's own postProcess), when active:

1. Introspect the just-loaded target table's columns (fresh, includes evolved columns).
2. Infer (when `inferTypes`): `IInferenceService.InferAsync` against the loaded table (the flow's own
   target connection reference; the service resolves secrets itself).
3. Resolve: `ColumnTransformResolver.Resolve(tableColumns, policy, inferredColumns)`.
4. `CREATE OR ALTER VIEW [schema].[v<Table>]` over the table via `TransformViewBuilder` +
   `ISchemaProvider.ExecuteDdlAsync`. A failure fails the run (downstream flows read the view; a stale
   or missing view must be loud), though the committed load stands.
5. Stamp the resolved columns (+ view name) on the FlowResult -> run.json -> catalog `detected` rows.

The downstream ingestion flow (view -> raw -> target) is ordinary chained YAML, ordered by
lineage/batch waves; nothing new to build there.
