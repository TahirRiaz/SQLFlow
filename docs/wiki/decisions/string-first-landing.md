---
id: wiki-string-first-landing
title: "Landing is string-first, and typing is a separate stage"
type: decision
summary: "Why every non-Parquet source lands as strings and typing is deferred, what was rejected, and how an empty cell becomes NULL rather than a blank."
keywords:
  - string-first
  - landing
  - type inference
  - raw table
  - pre-ingestion transform
  - parquet
  - empty string
  - null
sourceRefs:
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Core/Engine/TypeInferencer.cs
  - src/SqlFlow.Core/Engine/InferenceService.cs
  - src/SqlFlow.SqlServer/SqlServerColumnProfiler.cs
referenceRefs:
  - concept-type-inference
  - concept-pre-ingestion-transform
  - concept-provenance-and-row-keys
related:
  - wiki-design-doc-drift
updated: 2026-09-09
---

# Landing is string-first, and typing is a separate stage

A file source lands into its raw table with every column as a string. Nothing is parsed, coerced,
or rejected on the way in. Types are decided afterwards, by profiling the landed table and emitting
a typed view over it.

The one exception is Parquet, whose own schema already carries real types, so a Parquet-sourced
table needs no inference at all.

For the mechanics of how types are then chosen, see
[concept-type-inference](../../reference/concepts/type-inference.md); for how the typed view is
built, see [concept-pre-ingestion-transform](../../reference/concepts/pre-ingestion-transform.md).
This page is only about why the split exists.

## Why

**A load must not be able to fail on one bad cell.** If the reader parsed as it read, a single
malformed date in row 400,000 would abort a load that had already moved 399,999 good rows. Landing
strings makes the load a pure transport step: it either moved the bytes or it did not. Every
interpretation question is deferred to a stage that can be re-run cheaply against data already on
the server.

**The type decision must be revisable without re-fetching.** Getting a type wrong is normal,
especially against a source whose locale conventions are not obvious. Because the raw table still
holds the original strings, a wrong decision is corrected by rewriting a view, not by re-downloading
history from a third party that may no longer serve it. This matters most for sources that are dead
or that only expose current state, where the landed bytes are the only copy that will ever exist.

**SQL Server is the oracle, so profiling must happen where the data already is.** Types are chosen
by counting `TRY_CONVERT` acceptance server-side over the real landed values. That is only possible
if the values are already in a table, in their original textual form. A reader that had parsed them
in-process would have destroyed the evidence the profiler needs, and would have made the parse
decision using .NET semantics that the later `CONVERT` may not reproduce.

## What was rejected

**Parsing in the reader, using the declared schema.** This is what a conventional ETL tool does. It
was rejected because it makes the source contract authoritative over the actual bytes. When a
vendor quietly changes a date format, a parse-on-read pipeline fails the load; a string-first
pipeline lands the data and surfaces the change as a type-inference result you can look at.

**Landing typed but nullable, coercing failures to NULL.** This loses the original value silently,
which is the worst outcome: the row is present, the load reports success, and the evidence needed to
diagnose the drift is gone.

## The consequence that bites

An empty cell does not land as an empty string. `FileSourceReaderBase.CoerceCell`
(src/SqlFlow.Sources/FileSourceReaderBase.cs:990) turns a zero-length string into `null`, so a blank
field in a CSV becomes `NULL` in the raw table.

This is coherent with the string-first design, since `NULL` and `''` would otherwise be
indistinguishable after trimming, and the profiler already treats empty as NULL. It is also the
single most common way a ported legacy predicate silently changes meaning: a legacy system that
stored `''` would treat `WHERE col IS NOT NULL` as a no-op that keeps every row, while against a V3
raw table the same predicate drops every blank row. When porting a legacy filter, check whether it
was written against a `''` convention before assuming it is a no-op.
