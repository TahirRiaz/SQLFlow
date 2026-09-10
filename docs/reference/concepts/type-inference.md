---
id: concept-type-inference
title: "Type inference: profiling, decision rules, and server locale"
type: concept
summary: How SQLFlow profiles raw string columns with TRY_CONVERT, picks SQL types via locale-aware decision rules, and emits the typed transform expressions.
keywords:
  - try_convert
  - profiling
  - typeinferencer
  - culture
  - locale
  - threshold
  - sampling
  - onconverterror
related:
  - flow-transform
  - cli-infer
  - concept-pre-ingestion-transform
sourceRefs:
  - src/SqlFlow.Core/Engine/TypeInferencer.cs
  - src/SqlFlow.Core/Engine/InferenceService.cs
  - src/SqlFlow.Core/Engine/LocaleConversion.cs
  - src/SqlFlow.Core/Model/TypeInference.cs
  - src/SqlFlow.SqlServer/SqlServerColumnProfiler.cs
  - src/SqlFlow.SqlServer/SqlServerLocaleProvider.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/InferSpecLoader.cs
  - src/SqlFlow.Cli/Program.cs
---

# Type inference: profiling, decision rules, and server locale

Type inference turns a table of raw strings into correctly typed columns. File loads land their columns as strings in the raw table, with one exception: Parquet's own schema already supplies real types, so a Parquet-sourced table needs no inference. For the string-landing sources, the inference layer profiles each column against SQL Server's own conversion functions, decides the optimal SQL type per column, and emits the `CONVERT`/`TRY_CONVERT` SELECT expressions that produce it. Inside a pipeline flow, those expressions become the pre-ingestion transformation view `[schema].[v<Table>]` over the just-loaded table; standalone, they become a JSON `InferenceReport` with a runnable transform SELECT.

The design principle: SQL Server is the oracle. Every candidate type is counted by running `TRY_CONVERT` server-side over the actual data, so a chosen type is by construction convertible by the same engine that will later execute the conversion. The profiler, the emitted transform, and the validator all build their SQL from the same code (src/SqlFlow.Core/Engine/LocaleConversion.cs), so the probe, the load, and the validation are guaranteed identical.

## How it works

An inference run (src/SqlFlow.Core/Engine/InferenceService.cs) proceeds in four steps:

1. **Introspect.** The target table's columns are read. If the table does not exist the run fails with `Table [<schema>].[<table>] does not exist - load the raw data before inferring.`
2. **Resolve one locale for the whole run** (see below). Ambiguous dates and numbers are interpreted consistently across every column instead of flipping per column.
3. **Profile each column server-side** in one set-based pass per column (src/SqlFlow.SqlServer/SqlServerColumnProfiler.cs). Values are trimmed and empty strings treated as NULL, then `TRY_CONVERT` acceptance is counted per candidate: bigint, uniqueidentifier, a boolean token set, each locale-preferred date and datetime `CONVERT` style, and each numeric convention (locale-normalized, plus invariant when the locale uses a `,` decimal). The pass also captures min/max integer values, max length, leading-zero integer counts, and decimal precision/scale. When `sample > 0` the profiling query gets a `TOP (n)` clause; `0` means full scan.
4. **Decide and emit.** The pure `TypeInferencer` (src/SqlFlow.Core/Engine/TypeInferencer.cs) picks a type from the counts and emits the SELECT expression for it.

### Decision order

For each column, the first rule whose convertible count meets the threshold wins:

1. **bit**: detected by the token set `0, 1, true, false, yes, no, y, n` (case-insensitive), not by `TRY_CONVERT(bit, ...)`, which would coerce any number to 1.
2. **Integer** (`tinyint`/`smallint`/`int`/`bigint`): skipped when `preserveLeadingZeros` is on and any value is a leading-zero integer (zip codes, identity-like codes). Sizing comes from the observed min/max: `0..255` is `tinyint`, `-32768..32767` is `smallint`, the 32-bit range is `int`, otherwise `bigint`.
3. **decimal**: the numeric candidates are scanned locale-preferred first (locale convention, then invariant); the first that meets the threshold wins. The type is `decimal(p, s)` where `s` is the max observed scale and `p` is `maxIntegerDigits + s`, both clamped to 38 (scale further capped at precision).
4. **float**: only catches what decimal cannot, for example scientific notation.
5. **datetime2**: first locale-preferred datetime style that meets the threshold; the emitted expression carries the style code.
6. **date**: first locale-preferred date style that meets the threshold.
7. **uniqueidentifier**.
8. Otherwise the column stays a string: `varchar(MaxLen)` with the observed max length clamped to `1..8000`, and the select expression is the raw column, unconverted.

For the date and numeric families the inferencer takes the FIRST locale-preferred candidate that meets the threshold, not the candidate with the highest count. That is deliberate: an ambiguous value like `01/02/2026` converts under several styles, so picking by count would let per-column noise flip the interpretation. Preferring the locale's order keeps every column in a file consistent, and only genuinely foreign-formatted columns fall through to a later candidate.

The profiler also guards date candidates with a shape check (`x LIKE '%[0-9][0-9][0-9][0-9]%'`): a genuine date carries a four-digit year, which stops short codes like `11-800` from being silently accepted as dates by SQL Server's lenient parser.

### Threshold

`threshold` (default `1.0`) is the fraction of non-null profiled values that must convert before a type is chosen. At `1.0` every non-null value must convert; at `0.95` a column with up to 5 percent stragglers still gets typed (what happens to the stragglers is governed by `onConvertError`). Columns with zero non-null values keep their string type.

### Emitted expression and error modes

A typed column's select expression is:

```sql
<CONVERT|TRY_CONVERT>(<type>, <input>[, <style>])
```

where `<input>` is the bracketed raw column, wrapped in the locale numeric-normalization SQL for numeric types (see below), and `<style>` is the winning `CONVERT` style code for date types. `onConvertError` picks the function:

| Mode | Function | Behavior |
| --- | --- | --- |
| `fail` | `CONVERT` | Strict: a value that does not convert halts the load. Model default. |
| `silentNull` | `TRY_CONVERT` | Lenient: bad values become NULL. |
| `keepString` | `TRY_CONVERT` | Only converts columns where 100 percent of non-null values convert; any column short of that keeps the raw string, unconverted. |

Defaults differ by entry point: the `TypeInferencePolicy` model and the standalone infer spec default to `fail`, but the flow YAML loader (src/SqlFlow.Yaml/YamlFlowLoader.cs, `MapInference`) falls back to `silentNull` when a `transform:` block is present without the `onConvertError` key.

## Server locale resolution

One locale binds the whole inference run: date-component ordering (`Ymd`, `Dmy`, `Mdy`) plus the decimal and grouping separators.

- **Server-resolved (default).** `SqlServerLocaleProvider` (src/SqlFlow.SqlServer/SqlServerLocaleProvider.cs) resolves it in one round-trip: the date order from `sys.dm_exec_sessions.date_format` for the current session and the decimal convention from the culture behind `SERVERPROPERTY('LCID')`. Unknown or invariant LCIDs fall back to invariant numerics while keeping the session date order.
- **Explicit override.** A BCP-47 culture name (for example `nb-NO`) on the policy overrides the server locale with no database round-trip; the date order is read from the culture's short date pattern. An unrecognized name fails with `Inference culture '<x>' is not a recognised culture name.` The override is exposed as the `culture` key of the standalone infer spec; the flow `transform:` block has no culture key and always uses the server-resolved locale.
- **Fallback.** `ServerLocale.Invariant`: culture `invariant`, `Ymd` date order, `.` decimal, `,` grouping.

### Date style candidates

The locale's date order selects the ordered list of `CONVERT` style codes to probe, always ending in unambiguous ISO styles so genuinely ISO columns still convert (src/SqlFlow.Core/Engine/LocaleConversion.cs):

| Date order | `date` styles | `datetime2` styles |
| --- | --- | --- |
| `Dmy` | 104, 103, 105, 23, 101 | 104, 103, 105, 121, 120, 101 |
| `Mdy` | 101, 110, 23, 103 | 101, 110, 121, 120, 103 |
| `Ymd` | 23, 102, 111, 103, 101 | 121, 120, 102, 103, 101 |

For example style 104 is `dd.mm.yyyy`, 103 is `dd/mm/yyyy`, 23 is ISO `yyyy-mm-dd`, and 121/120 are the ISO datetime styles.

### Numeric normalization

Numbers are normalized to a `.`-decimal, no-grouping string before conversion, rendered as nested `REPLACE` SQL by `LocaleConversion.NumericInput`:

- For a `.`-decimal locale (en-US, invariant), commas, spaces, and non-breaking spaces are unambiguously grouping and are stripped unconditionally.
- For a `,`-decimal locale (for example nb-NO), `.` and spaces are treated as grouping ONLY when a comma is present in the value, guarded by a `CASE WHEN CHARINDEX(',', ...) > 0` wrapper. Without a comma the value is left untouched, so dotted dates like `25.12.2026` and invariant decimals like `12.5` are not mangled, while genuine Norwegian numbers like `1.234,56` still parse.

When the locale uses a `,` decimal, the profiler probes both the locale convention and the invariant one; the inferencer prefers the locale candidate and falls back to invariant. For a `.`-decimal locale the two are identical, so only one is probed.

## The transform view (pipeline integration)

Inside a flow, inference is part of the `transform:` block. When the flow generates a view, the run's `transform.view` stage emits `CREATE OR ALTER VIEW [schema].[v<Table>]` over the just-loaded table; downstream chained flows read that view as their source, which is how raw string tables get correct data types.

- `transform.inferTypes` (default `false`) enables inference.
- `transform.generateView` (default `true`) controls whether the view post-process runs. Nothing is generated when neither inference nor authored `transform.columns` entries ask for any typing (`GeneratesView` in src/SqlFlow.Core/Model/TypeInference.cs).
- Authored `transform.columns[]` entries override what inference would pick for the same column; inference fills in the columns no entry names.

The view refresh runs after `source.complete`, so a view failure never leaves source files un-finalized: the load stands, the files are marked ingested, and a re-run regenerates the view idempotently. The failure still fails the run, because downstream flows must not read a stale view.

Full key-by-key reference for the block, including authored column transforms (`name`, `expr`, `as`, `type`, `order`, `virtual`, `excludeFromView`) and their validation errors, is in [transform](../flow/transform.md).

## Configuration touchpoints

**Flow YAML** (`transform:` block, parsed by src/SqlFlow.Yaml/YamlFlowLoader.cs):

| Key | Default | Effect on inference |
| --- | --- | --- |
| `transform.inferTypes` | `false` | Enable inference. |
| `transform.onConvertError` | `silentNull` (when the block is present) | `silentNull`, `fail`, or `keepString`. |
| `transform.threshold` | `1.0` | Fraction of non-null values that must convert. |
| `transform.sample` | `0` | Profiling row cap; `0` is a full scan. |
| `transform.preserveLeadingZeros` | `true` | Keep zero-padded integers as strings. |
| `transform.generateView` | `true` | Generate the typed view post-process. |
| `transform.columns` | `[]` | Authored per-column transforms; override inference per column. |

**Standalone infer spec** (`*.infer.yaml`, parsed by src/SqlFlow.Yaml/InferSpecLoader.cs): `connection` (required), `table` (required, `dbo.Orders` or separate `schema` + `table`), `onConvertError` (default `fail`), `threshold`, `sample`, `preserveLeadingZeros`, `culture` (locale override, infer spec only).

**CLI** (src/SqlFlow.Cli/Program.cs): `sqlflow infer <spec.infer.yaml>` profiles an already-loaded table and prints the JSON report. By default it also validates: the report carries the transform SELECT plus per-column fit. `--no-validate` skips validation; `--out <path>` (or `-o`) writes the report to a file instead of stdout.

## Validation

`ValidateAsync` reruns the exact conversions against the real data and counts, per converted column, the non-null values that a `TRY_CONVERT` would silently null. Each column gets a status:

- `ok`: every non-null value converts.
- `lossy`: some values would become NULL (`silentNulls > 0`), with a `fitPercent` of the surviving fraction.
- `empty`: the column has no non-null values.
- `kept-string`: the column was not converted at all.

The report's `validation.isValid` is true when no column is `lossy`.

## Example

Standalone spec (samples/infer/orders.infer.yaml):

```yaml
# Standalone datatype-inference job. Reads a loaded table, finds the
# optimal type per column, and outputs JSON.
connection: ${env:SQLFLOW_DW}
table: dbo.Orders              # or: schema: dbo  +  table: Orders

onConvertError: silentNull     # silentNull | fail | keepString
threshold: 1.0                 # fraction of non-null values that must convert
sample: 0                      # 0 = full scan
preserveLeadingZeros: true
```

```bash
sqlflow infer orders.infer.yaml --out types.json
```

The same policy inside a flow, driving the generated `[stage].[vOrders]` view:

```yaml
transform:
  inferTypes: true
  onConvertError: silentNull
  threshold: 1.0
  sample: 0
  preserveLeadingZeros: true
```

Given a `dmy` server locale, a column of values like `1.234,56` infers as `decimal(6, 2)` with a select expression of the shape:

```sql
TRY_CONVERT(decimal(6, 2),
    CASE WHEN CHARINDEX(',', [Amount]) > 0
         THEN REPLACE(REPLACE(REPLACE(REPLACE([Amount], '.', ''), ' ', ''), NCHAR(160), ''), ',', '.')
         ELSE [Amount] END)
```

and a column of `25.12.2026` dates infers as `datetime2` with `TRY_CONVERT(datetime2, [OrderDate], 104)`: the `datetime2` family is checked before `date` in the decision order, and a bare date string converts under both.

## See also

- [transform block reference](../flow/transform.md)
- [sqlflow infer](../cli/infer.md)
- [Pre-ingestion transform views](./pre-ingestion-transform.md)
