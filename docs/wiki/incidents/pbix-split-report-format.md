---
id: wiki-pbix-split-report-format
title: "A report saved by a current PowerBI Desktop extracted with no visuals at all"
type: incident
summary: "Power BI Desktop replaced Report/Layout with a tree of per-visual documents, so every question a report stated vanished from extraction, with no error."
keywords:
  - pbix
  - Report/Layout
  - Report/definition
  - PBIR
  - visual layer
  - report format
  - silent
  - vendor format change
sourceRefs:
  - tools/pbix-extract/src/reportlayout.c
  - tools/pbix-extract/src/reportlayout.h
  - tools/pbix-extract/src/datamodel.c
  - tools/pbix-extract/src/datamodel.h
  - tools/pbix-extract/tests/test_reportlayout.c
rawRefs:
  - POWERAI.md
referenceRefs:
  - flow-subscribers
related:
  - wiki-powerbi-model-entity-resolution
updated: 2026-09-13
---

# A report saved by a current PowerBI Desktop extracted with no visuals at all

**What happened.** The sample AdventureWorks report was opened in a current PowerBI Desktop, edited,
and saved. Extracting it then reported `3 pages, 5 visuals` as `0 pages, 0 visuals`, with one line on
standard error: the file `contains no 'Report/Layout' part`. The model half extracted perfectly, so
the output looked substantially complete. Every business question the report stated, and every
rendered `SELECT`, was simply gone.

**The mechanism.** Power BI Desktop no longer writes the visual layer as one `Report/Layout` part.
It writes a tree instead:

```
Report/definition/pages/pages.json                      page order
Report/definition/pages/<page>/page.json                one per page
Report/definition/pages/<page>/visuals/<id>/visual.json one per visual
```

The reader knew only the older shape, so an absent `Report/Layout` was the end of the story. Nothing
about the situation was malformed: the file was a valid, current `.pbix` that a person could open and
use normally.

**Why the warning made it worse.** `pbix_read_member` is shared by both halves of the tool, and its
"member not found" message was written for the caller that came first. It explained EVERY missing
part as a live connection to a published dataset, which is true only of a missing `DataModel`. So the
one diagnostic a reader got pointed at a model that was in fact present and fully read, and said
nothing about the report layer that was actually missing. A shared error path that names one specific
cause will eventually be reached by a caller that cause does not fit.

## What generalizes

**A reader pinned to one version of a format someone else controls has an expiry date, and it expires
quietly.** The failure surfaced as a smaller number, not as an error: zero visuals is
indistinguishable at a glance from a report that genuinely has none, which is a legitimate state the
tool deliberately supports. The tool had a real refusal discipline for expression nodes it could not
represent, recording each one in `reportWarnings` rather than dropping it, and none of that helped
here, because the loss happened one layer above where the refusals live. Dropping a visual is loud;
never finding the visuals is silent.

**The vocabulary survived the restructuring.** The new documents carry the same expression language
as the old: `Column`, `Measure`, `HierarchyLevel`, `Aggregation`, `In`, `Comparison`, and the
`SourceRef` shape. Only the packaging around it changed. So the fix reused every expression, filter
and SQL-rendering function verbatim and translated only the surrounding structure, which is the
difference between adding a reader and adding a second implementation to keep in step. It is worth
checking for that before assuming a format change is a rewrite.

Three structural differences did need handling, and the third is the one with a consequence:

- a field's expression sits INLINE under its role, rather than behind a `queryRef` pointing into a
  separate `prototypeQuery`;
- filters are a native array under `filterConfig`, rather than JSON nested as a string;
- there is no query-level `From`, because references name their entity directly instead of binding an
  alias. The rendered `FROM` is therefore reconstructed from the entities a visual's fields actually
  name. That list is the visual's consumption lineage, so leaving it empty would have produced SQL
  that parsed and named no tables, which is a worse failure than not rendering at all.

**Both shapes have to stay supported, and only one of them is now exercised by a real file.** Reports
saved by older Desktop versions are still `Report/Layout`, and there is no converting them. The
sample, having been re-saved, now exercises only the split reader; the older path is held by synthetic
fixtures in the tool's suite. That is the reverse of the situation the day before, which is a reminder
that "verified against a real file" is a claim with a date on it.
