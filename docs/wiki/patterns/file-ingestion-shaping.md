---
id: wiki-file-ingestion-shaping
title: "Pattern: turning landed files into tables (nested JSON, dialect CSV, provenance)"
type: pattern
summary: "Shaping nested documents, surviving CSV dialects, dating a file from its name, and pinning the columns type inference keeps getting wrong."
keywords:
  - explodePaths
  - includePaths
  - pathAliases
  - flattening
  - csv dialect
  - fileDate
  - includeRowNumber
  - schema overrides
  - defaultColDataType
rawRefs:
  - docs/flattener-memory-postmortem.md
referenceRefs:
  - concept-json-xml-flattening
  - concept-file-source-pipeline
  - concept-provenance-and-row-keys
  - source-type-csv
  - source-type-json
related:
  - wiki-landing-and-protection
  - wiki-string-first-landing
  - wiki-pattern-catalog
updated: 2026-09-09
---

# Pattern: turning landed files into tables

**The problem.** A landed file is not a table. A JSON document has arrays nested inside arrays; a
vendor CSV uses semicolons, quoted fields, and a Latin-1 encoding; an XLSX has a header row three
rows down. The file flow's job is to produce a rectangle without losing what the document meant.

Key semantics live in the reference corpus ([json-xml-flattening](../../reference/concepts/json-xml-flattening.md),
[file-source-pipeline](../../reference/concepts/file-source-pipeline.md), and the per-format pages).
This page is about which shape to reach for.

## Nested documents: explode, include, alias

Three options do most of the work, and they answer different questions.

**`explodePaths`** turns an array into rows. This is the decision that sets the grain of the output
table: exploding `$.trips[*].legs[*]` gives one row per leg, not per trip. Exploding two sibling
arrays multiplies them, which is almost never what a consumer wants.

**`includePaths`** whitelists which paths are carried. A wide document flattened whole produces
hundreds of columns nobody reads. This is also a correctness control, not just tidiness: a
`jsonPath` sitting under a non-whitelisted ancestor lands nothing at all, silently, so an
`includePaths` list that is too narrow reads as an empty column rather than an error.

**`pathAliases`** renames a deep path to a sane column name, so the table's contract does not encode
the vendor's document structure.

**There are no filter predicates.** The reader has no JSONPath filter expressions: you cannot select
`$.items[?(@.type=='X')]`. When the shape needs a filter, land everything and express the filter in
the typed view instead.

**Watch the grain when a document is large.** Exploding a big document is where memory goes; the
[flattener memory postmortem](../../flattener-memory-postmortem.md) is the recorded case.

## CSV dialects

`delimiter`, `textQualifier`, `firstRowHasHeader`, `srcEncoding`, and `maxBufferSize` cover the
dialect surface. Two notes worth having in advance:

- **Encoding is not guessable and failing loudly is correct.** Legacy Norwegian feeds are frequently
  Latin-1 or windows-1252; reading them as UTF-8 mangles every Norwegian character, and the damage is
  only visible if you look at the right row. An encoding the runtime cannot resolve fails the run
  rather than falling back to UTF-8.
- **A tab delimiter is written as an escaped `\t`** in YAML, and header spaces become underscores in
  column names.

## Provenance: which file, which row

`includeRowNumber` and `includeFileLineNumber` add the row's position, and the dataset/file columns
record which file it came from. This is what makes a row traceable back to a landed byte, and it is
what makes a merge key stable.

**Provenance in a merge key is a trap on a rolling-window feed.** If the key includes the source file
name, and the source re-exports overlapping windows under new file names, the same logical row lands
again under a new key. Row count then tracks *files loaded* rather than facts, and grows without
bound. Diagnose the file scope before blaming the key.

## Dating a file from its name

`source.options.fileDate.from` and `fileDate.pattern` extract the covered date from the **file name**.
Use them whenever the name encodes a date, which is to say almost always.

The reason is that an object store's modified time is not provenance: any copy, migration, or
re-upload rewrites it, so a pipeline that partitions or filters on modified time silently re-dates
history the first time the lake is moved. Nine folders in the estate do this.

## Pinning what inference gets wrong

Landing is string-first and types are inferred afterwards
(see [string-first landing](../decisions/string-first-landing.md)). Two escape hatches exist for when
inference is wrong rather than merely conservative:

- **`schema.overrides`** pins a named column to a declared type. Reach for it when a column is a
  nested JSON blob that must stay text, or when sampling picks a type that a later file violates.
  Exemplars: `hentmeg/` (five nested objects held as text), `voi/`, `ryde/` (route geometry).
- **`defaultColDataType`** sets the fallback for everything not otherwise decided, which is how a
  source with genuinely free-text columns avoids per-column pinning. Eight folders use it.

## Empty is not blank

A zero-length cell lands as `NULL`, not `''`. A legacy predicate written as `WHERE col IS NOT NULL`
against a system that stored `''` was a no-op there and drops every blank row here. This is the most
common way a ported filter silently changes meaning; see
[string-first landing](../decisions/string-first-landing.md).

## Production exemplars

| Flow folder | Shape |
| --- | --- |
| `cyclehire/`, `fjord1/`, `ticketapp/` | `explodePaths` + `pathAliases` on nested trip documents |
| `hentmeg/`, `voi/`, `ryde/` | `schema.overrides` pinning nested blobs and geometry to text |
| `entur/`, `ferde/`, `reisefrihet_nets/` | positional and dialect CSV with explicit header handling |
| 9 folders | `fileDate.from` deriving the covered date from the file name |
