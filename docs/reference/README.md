# SQLFlow V3 reference corpus

Machine-readable reference documentation for SQLFlow V3, generated
from and verified against the source code in this repository. Built to be consumed by three
downstream tools: an MCP server, a `.flow.yaml` language server (LSP), and a VSCode extension.

Every fact in this corpus traces to a file and line in the codebase (see each page's
`sourceRefs` frontmatter). Where an older design document, the JSON schema, or the README
disagreed with the shipped code, the code won; known drift is called out inline on the
relevant page rather than silently corrected elsewhere.

## Layout

```
docs/reference/
  manifest.json                  machine index over every page (MCP search surface)
  flow/keys.json                 per-attribute census of the file-flow YAML model (LSP surface)
  cli/<command>.md               one page per top-level CLI command (16 pages)
  flow/<section>.md              one page per top-level section of the file-flow YAML (plus overview)
  flow/<flowtype>.md             one page per non-file flow kind: ing, exp, sp, inv, hc, scm, batch
  flow/source-types/<type>.md    one page per source format: csv, json, xml, parquet, xls, duckdb
  concepts/<slug>.md             cross-cutting concepts (24 pages)
  guides/<slug>.md                task-oriented walkthroughs (12 pages)
```

85 pages total: 16 `cli-command`, 27 `flow-reference`, 6 `source-type`, 24 `concept`, 12 `guide`.

Every page carries YAML frontmatter: `id`, `title`, `type`, `summary`, `keywords`, `related`,
`sourceRefs`, plus `yamlPath` (flow-reference / source-type pages) or `cliCommand`
(cli-command pages).

## manifest.json (MCP server surface)

Shape:
```json
{
  "version": 1,
  "product": "SQLFlow V3",
  "docs": [
    { "id": "...", "path": "...", "title": "...", "type": "...",
      "summary": "...", "keywords": ["..."], "yamlPath": "...", "cliCommand": "...",
      "related": ["..."], "sourceRefs": ["..."] }
  ]
}
```

Recommended MCP tool surface built directly from this file:

- `search_docs(query)`: match query tokens against `summary` + `keywords` + `title`, return
  ranked `{id, path, summary}`. No embeddings needed at this corpus size; keyword or
  BM25-style matching over the manifest fields is sufficient.
- `get_doc(id)`: look up `path` in the manifest, read and return that page's markdown body.
- `get_doc_by_yaml_path(yamlPath)`: filter the manifest for a matching `yamlPath` (exact,
  then longest-prefix) to jump from a YAML key straight to its page.
- `get_doc_by_cli_command(command)`: same idea keyed on `cliCommand`.
- `related_docs(id)`: resolve `related` ids through the manifest for "see also" expansion.

Embed the whole corpus, every page body plus the manifest, as the MCP server's static
content; there is no need to fetch anything at runtime.

## flow/keys*.json (LSP surface)

Nine census files, 421 attributes total, one per document kind plus one shared file:

| File | flowType | Attributes | Covers |
| --- | --- | --- | --- |
| `keys.json` | (none, the default) | 162 | The file flow: `name`, `flowType`, `batch`, `schedule`, `source`, `target`, `schema`, `load`, `transform`, `preProcess`, `postProcess`, `desiredIndexes`, `incremental`. |
| `keys.ing.json` | `ing` | 112 | Table-to-table ingestion: source/target endpoints, load/matchKeys/change/systemColumns, assertions/surrogateKeys/virtualColumns, schema/incremental/initLoad, versioning. |
| `keys.exp.json` | `exp` | 29 | File export: source endpoint plus filter, target file shape, chunk policy. |
| `keys.sp.json` | `sp` | 11 | Stored-procedure execution: the procedure endpoint. |
| `keys.inv.json` | `inv` | 12 | ADF/Automation invoke: the `invokes` block. |
| `keys.hc.json` | `hc` | 27 | Health-check: target endpoint, metrics, the `ml` block. |
| `keys.scm.json` | `scm` | 28 | Source-control snapshot: source endpoint, repository, object filters. |
| `keys.batch.json` | `batch` | 15 | Ordered multi-flow batch: member selection, wave and error policy. |
| `keys.shared.json` | (cross-cutting) | 25 | Blocks reused by several kinds: `connections`, `servicePrincipals`, the `preInvoke`/`postInvoke` hook shape. |

Each file shares one shape: `{ version, product, flowType, generatedFrom, naming, keyCount,
keys: [ { path, type, required, default?, enumValues?, description, appliesWhen?, definedIn,
validation? } ] }`. `flowType` is `null` on `keys.shared.json`.

`path` is a dot-path relative to that document's root (for example `source.options.header` in
`keys.json`, or `load.keyColumns` in `keys.ing.json`), list elements use `[]` (for example
`transform.columns[].name`), and dictionary-valued keys use a bracketed literal (for example
`source.options["fileDate.from"]`). Naming is camelCase per the shared YamlDotNet
`CamelCaseNamingConvention` used by every loader; see any file's `naming` field for the exact
rule and its two carve-outs (map keys are never renamed; enum-like values are parsed
case-insensitively with `-`/`_` stripped for `load.mode` and `transform.onConvertError`).

The root-level `flowType` discriminator itself (the `ing`/`exp`/`sp`/`inv`/`hc`/`scm`/`batch`
enum that selects which of these nine files applies to a given document) is documented once,
centrally, in `keys.json` under the path `flowType`; it is intentionally not repeated in the
other eight files, to avoid two copies drifting apart. An LSP should read the document's own
`flowType` value first, then select the matching `keys*.json` file (or `keys.json` when the
key is absent) before resolving any other path against it.

LSP features map directly onto whichever file matches the open document's `flowType`:

- **Hover**: resolve the token path under the cursor to a `keys` entry; render its type,
  required/default, `enumValues`, and description.
- **Completion**: at a given path, offer child keys whose `path` starts with `<parent>.` one
  segment deep; filter out entries whose `appliesWhen` names a sibling discriminator (for
  example `source.type`) that does not match the value already authored in the document.
- **Diagnostics**: flag a `required: true` path missing from the document; flag a value
  outside `enumValues` (case-insensitively, honoring the separator-stripping noted in
  `naming` where it applies); flag a key with no matching census entry as an unknown-key
  warning, not a hard error, since the deserializer uses `IgnoreUnmatchedProperties`.
- **Code actions**: an insert-defaults snippet built from every `default` at the current
  path depth.

## VSCode extension

A thin client: register the `.flow.yaml` language id, start the LSP client pointed at the
language server above, and add a "SQLFlow: Search Docs" command that calls the MCP server's
`search_docs` / `get_doc` tools (or reads the bundled manifest directly when offline) to
render a page in a webview.

## Maintenance

`keys.json` and the per-flow-kind `keys.<flowtype>.json` census files are generated snapshots
of the model in `src/SqlFlow.Core/Model`, `src/SqlFlow.Yaml`, and the matching runner
projects. Re-derive them from source whenever the flow model changes; do not hand-edit an
entry without re-verifying it against the code, since silent drift between this corpus and
the code it describes is exactly what it was built to eliminate.

`manifest.json` is mechanically derived from every page's frontmatter and never hand-edited.
After adding, removing, or renaming a page, or after changing any page's frontmatter, run:

```bash
python docs/reference/build_manifest.py
```

It validates required fields, derives a summary from the body when one is missing, prunes
`related` ids that no longer resolve, and reports every fix it made. Requires PyYAML
(`pip install pyyaml`).
