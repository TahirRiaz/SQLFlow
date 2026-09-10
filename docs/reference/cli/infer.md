---
id: cli-infer
title: sqlflow infer and the .infer.yaml spec
type: cli-command
summary: Profile a loaded SQL Server table from a standalone .infer.yaml spec, infer the optimal type per column, and emit the JSON inference report.
keywords:
  - infer
  - type inference
  - infer.yaml
  - standalone spec
  - onconverterror
  - threshold
  - silent null
  - inference report
cliCommand: infer
related:
  - concept-type-inference
  - flow-transform
  - cli-validate
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Yaml/InferSpecLoader.cs
  - src/SqlFlow.Core/Abstractions/IInferenceService.cs
  - src/SqlFlow.Core/Engine/InferenceService.cs
  - src/SqlFlow.Core/Engine/TypeInferencer.cs
  - src/SqlFlow.Core/Model/InferenceRequest.cs
  - src/SqlFlow.Core/Model/InferenceReport.cs
  - src/SqlFlow.Core/Model/TypeInference.cs
  - src/SqlFlow.Core/SqlFlowException.cs
  - samples/infer/orders.infer.yaml
---

# sqlflow infer

## Synopsis

```bash
sqlflow infer <spec.infer.yaml> [--no-validate] [--out|-o <file>]
```

## Description

Runs standalone datatype inference against a table that is already loaded in SQL Server. The command reads a dedicated inference spec (a `.infer.yaml` document, a shape of its own, separate from a pipeline flow), profiles every column of the target table server-side, determines the optimal SQL type per column, and emits an `InferenceReport` as JSON.

By default the command runs the validating variant: it infers the types and then executes the conversions against the real data, so the report carries a per-column fit status and flags any column whose non-null values would silently become NULL under the inferred type. Pass `--no-validate` to skip that second pass and get the profile plus the runnable transform SELECT only.

The spec is loaded by `InferSpecLoader` (src/SqlFlow.Yaml/InferSpecLoader.cs) into an `InferenceRequest` (src/SqlFlow.Core/Model/InferenceRequest.cs); the inference itself is `InferenceService` (src/SqlFlow.Core/Engine/InferenceService.cs), which is decoupled from the pipeline engine and needs no metadata database. The argument must be an inference spec: a pipeline flow document does not have root-level `connection` and `table` keys and fails spec validation.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<spec.infer.yaml>` | yes | Path to the standalone inference spec. A missing argument prints the usage text and exits 1; a path that does not exist fails with `Inference spec not found: '<path>'.` |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--no-validate` | switch | off | Skip the validation pass. Runs `InferAsync` (profile plus transform SELECT) instead of `ValidateAsync`; the report's `validation` field is then `null`. |
| `--out`, `-o` | path | stdout | Write the JSON report to this file instead of stdout and print `Wrote inference report to <path>`. A following value that starts with a hyphen (other than a bare `-`) is read as the next flag, so the option is treated as absent. |
| `-v`, `--verbose` | switch | off | Global flag. Among other diagnostics, reports on stderr when a local `.sqlflow/env` file was applied. |
| `-h`, `--help` | switch | off | Print the usage text. Exits 0 when the spec path is also supplied; without one the missing-argument exit code 1 applies. |

## The .infer.yaml spec

Keys use camelCase; unknown keys are ignored. A minimal working spec (adapted from samples/infer/orders.infer.yaml):

```yaml
connection: ${env:SQLFLOW_DW}
table: dbo.Orders              # or: schema: dbo  +  table: Orders

onConvertError: silentNull     # silentNull | fail | keepString
threshold: 1.0                 # fraction of non-null values that must convert
sample: 0                      # 0 = full scan
preserveLeadingZeros: true
```

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `connection` | string | yes | none | Connection reference for the database holding the table, resolved through the secret resolver (for example `${env:SQLFLOW_DW}`). |
| `table` | string | yes | none | Table to profile. May be schema-qualified (`dbo.Orders`); brackets are stripped. |
| `schema` | string | no | `dbo` | Schema, used only when `table` is one-part. |
| `onConvertError` | string | no | `fail` | What a value that does not fit the inferred type does: `silentNull`, `fail`, or `keepString`. |
| `threshold` | number | no | `1.0` | Fraction of non-null values that must convert before a type is chosen (`1.0` = all). |
| `sample` | integer | no | `0` | Rows to sample when profiling; `0` = full scan. |
| `preserveLeadingZeros` | boolean | no | `true` | Keep integer-looking values with significant leading zeros (zip codes, IDs) as string. |
| `culture` | string | no | server locale | Explicit BCP-47 culture (for example `nb-NO`) overriding the server-resolved locale for date and numeric interpretation. |

An empty document fails with `<path>: the document is empty.`; malformed YAML fails with `<path>: invalid YAML - <parser message>`. All spec errors are `FlowValidationException` (a `SqlFlowException`, src/SqlFlow.Core/SqlFlowException.cs) and are prefixed with the spec path.

### connection

Required; a missing or blank value fails with `<path>: 'connection' is required.` The value is a reference, not a raw connection string with credentials: it is resolved through the secret resolver at run time (environment references such as `${env:SQLFLOW_DW}`). For local development, a git-ignored `.sqlflow/env` file supplies the referenced variables; it is searched from the spec's directory upward and the process environment always wins.

### table and schema

Required; a missing value fails with `<path>: 'table' is required.` Square brackets are stripped, then the name is split at the last dot: `dbo.Orders` and `[dbo].[Orders]` both yield schema `dbo`, table `Orders`. When the name is one-part, the `schema` key applies, defaulting to `dbo`; when the name is schema-qualified, the `schema` key is ignored.

### onConvertError

Matched case-insensitively against the `ConvertErrorMode` names (src/SqlFlow.Core/Model/TypeInference.cs) after stripping `-` and `_`, so `silentNull`, `silent-null`, and `silent_null` all parse. Allowed values:

- `silentNull`: bad values become NULL (the transform uses `TRY_CONVERT`). Lenient.
- `fail`: bad values raise a SQL error (the transform uses `CONVERT`). Strict, fail loud. The default.
- `keepString`: only convert columns that are 100% convertible; otherwise keep the raw string.

An invalid value fails with `<path>: 'onConvertError' has invalid value '<v>'. Allowed: SilentNull, Fail, KeepString.`

Note the default asymmetry: the standalone spec defaults to `fail` (a value that diverges from the profiled sample halts the load rather than silently becoming NULL), while a pipeline flow's `transform.onConvertError` defaults to `silentNull` (src/SqlFlow.Yaml/YamlFlowLoader.cs).

### threshold

Fraction of non-null values that must convert before a type is chosen; `1.0` means every non-null value must convert.

### sample

Row count to sample when profiling; `0` scans every row.

### preserveLeadingZeros

When true (the default), integer-looking values with significant leading zeros (for example `007`) stay string instead of becoming a numeric type.

### culture

Optional BCP-47 culture name that overrides the server-resolved locale for date and numeric interpretation; blank or absent means the locale configured on the server is resolved over the connection. An unrecognised name fails at run time with `Inference culture '<v>' is not a recognised culture name.`

## Behavior and output

The target table must already exist; otherwise the run fails with `Table [schema].[table] does not exist - load the raw data before inferring.`

The report is serialized with the shared execution JSON settings (indented, camelCase properties, enums as strings) and printed to stdout, or written to `--out`/`-o` with the confirmation `Wrote inference report to <path>`. Its shape (src/SqlFlow.Core/Model/InferenceReport.cs):

- `schema`, `table`: the profiled table.
- `onConvertError`: the effective error mode (`SilentNull`, `Fail`, or `KeepString`).
- `culture`: the locale the run bound to (the server's, or the explicit override).
- `dateOrder`: the date-component ordering applied (`Ymd`, `Dmy`, or `Mdy`).
- `columns[]`: per column, the inferred `dataType`, the `selectExpression` that performs the conversion, whether it was `converted`, the SQL `style` for date conversions, the `numericFormat` convention (`Locale` or `Invariant`, null for non-numeric), and the `sampled`/`total` row counts.
- `transformSelect`: a runnable SELECT applying every column's conversion against the table, to eyeball or execute before committing to the typed schema.
- `validation`: null with `--no-validate`; otherwise `isValid` plus per-column `nonNull`, `silentNulls`, `fitPercent`, and `status`. Status is `ok` (converts cleanly), `lossy` (some non-null values would silently null out; `isValid` is false when any column is lossy), `kept-string` (no conversion applied), or `empty` (no values to evaluate).

Abridged example report:

```json
{
  "schema": "dbo",
  "table": "Orders",
  "onConvertError": "Fail",
  "culture": "nb-NO",
  "dateOrder": "Dmy",
  "columns": [
    {
      "columnName": "OrderDate",
      "dataType": "date",
      "selectExpression": "CONVERT(date, [OrderDate], 104)",
      "converted": true,
      "style": 104,
      "numericFormat": null,
      "sampled": 12000,
      "total": 12000
    },
    {
      "columnName": "ZipCode",
      "dataType": "nvarchar",
      "selectExpression": "[ZipCode]",
      "converted": false,
      "sampled": 12000,
      "total": 12000
    }
  ],
  "transformSelect": "SELECT CONVERT(date, [OrderDate], 104) AS [OrderDate], [ZipCode] FROM [dbo].[Orders]",
  "validation": {
    "isValid": true,
    "columns": [
      {
        "columnName": "OrderDate",
        "dataType": "date",
        "nonNull": 12000,
        "silentNulls": 0,
        "fitPercent": 100,
        "status": "ok"
      },
      {
        "columnName": "ZipCode",
        "dataType": "nvarchar",
        "nonNull": 0,
        "silentNulls": 0,
        "fitPercent": 100,
        "status": "kept-string"
      }
    ]
  }
}
```

## Examples

Infer and validate, printing the JSON report to stdout:

```bash
sqlflow infer orders.infer.yaml
```

Write the report to a file:

```bash
sqlflow infer orders.infer.yaml --out types.json
# Wrote inference report to types.json
```

Profile only, skipping the validation pass (the report's validation field is null):

```bash
sqlflow infer orders.infer.yaml --no-validate -o types.json
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | Report produced (printed or written). A lossy validation result does not change the exit code; inspect `validation.isValid` in the report. |
| 1 | Missing file argument (usage is printed), spec not found, spec validation error, unrecognised culture, missing table, or any other `SqlFlowException`; the message is printed to stderr as `ERROR  <message>` with secret values redacted. |

## See also

- [Type inference](../concepts/type-inference.md): how the profiler and type candidates work.
- [The transform block](../flow/transform.md): the same policy keys inside a pipeline flow, where inference feeds the typed transformation view.
- [sqlflow validate](validate.md): validate a pipeline document.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
