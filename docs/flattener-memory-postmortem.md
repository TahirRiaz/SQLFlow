# Postmortem: the record flattener's memory profile

**Date:** 2026-08-20 / 2026-08-21
**Surfaced as:** `System.OutOfMemoryException` in `target.load`, flow `cyclehiresrel_batchinfo_01_xml`,
after 20m14s with 0 rows loaded.
**Scope:** `XmlPathFlattener` and `JsonPathFlattener` in `SqlFlow.Sources`. Any flow with more than one
explode path, or one explode over a large collection, was affected.

This note is written to be portable. The specific defects are C#, but none of the four lessons depends on
the language, the runtime or the storage engine, and they transfer directly to any pipeline that flattens
nested documents into rows.

---

## What happened

A nightly schedule fired three pre-ingestion flows over the same corpus of 6,219 XML settlement documents.
Two of the three failed with `OutOfMemoryException`. The third survived only because its incremental
watermark happened to exclude every file.

The trace named the trigger on its fourth line:

```
incremental: no prior watermark (target empty and no run history); loading all available files
```

so instead of a handful of new documents the run attempted the whole corpus, and died on one document.

## Why one document could exhaust memory

The flattener turns one record into one or more output rows. Repeating elements listed in `explodePaths`
multiply, so a document with independent repeating sections produces their **cross product**. In this corpus
the largest document holds 13,655 transactions and 74 fees, and the product is **1,010,470 rows** from 13,729
actual facts: a 74x amplification.

That alone is survivable. What was not survivable is what a row cost. `OrderedRow` looked like this:

```csharp
private readonly NamePlan _plan;                          // shared across rows - fine
private readonly List<string> _order;                     // 96 column NAMES, per row
private readonly Dictionary<string, string?> _values;     // hash table, per row
private readonly Dictionary<string, ColumnMeta> _meta;    // source path + flag, per row

public OrderedRow Clone() => new(
    _plan,
    [.. _order],                                          // deep copy
    new Dictionary<string, string?>(_values, ...),        // deep copy
    new Dictionary<string, ColumnMeta>(_meta, ...));      // deep copy
```

A 96-column row was therefore not 96 values. It was a list of 96 string references plus **two**
case-insensitive hash tables of 96 entries each, and `ExplodeCrossProduct` deep-copies all three once per
element of every exploded repeat. On the order of 10 KB per row where the values need under 1 KB.

At 1,010,470 rows that is roughly **12 GB for a single document**.

Note the schema was *already* shared: `_plan` is passed by reference. Column name, source path and
large-text are facts about a **column**, not about a row. Every row was carrying a private copy of the
schema anyway.

## The safety net measured the wrong thing

```csharp
public const int MaxRowsPerRecord = 1_000_000;
```

A cap counted in **rows** cannot bound memory, because a row is not a fixed size. With 96 wide columns the
process died at a fraction of the cap, so the guard never fired and the operator got a bare
`OutOfMemoryException` with no file name, no path and no row count. The guard existed and was useless.

## The fix

Make the row what it actually is: a bare value array positioned by the shared plan.

```csharp
private readonly NamePlan _plan;
private string?[] _values;

public OrderedRow Clone()
{
    var copy = new string?[_values.Length];
    Array.Copy(_values, copy, _values.Length);
    return new OrderedRow(_plan, copy);
}
```

The plan gained a stable ordinal per column and now owns the per-column metadata. One subtlety was worth
proving rather than assuming: the alias-coalesce rule ("add a column, upgrade a null, never overwrite a
non-null") relied on the dictionary distinguishing *absent* from *present-and-null*. An array cannot. It
turns out the rule collapses exactly to `if (cell is null) cell = value;` for all four cases, so the
behaviour is preserved without a per-row presence bitmap.

**Measured on the real corpus, after the change:**

| | Before | After |
|---|---|---|
| 863,568-row document | OOM | completes, **1,520 MB** peak, 21.6s |
| 1,010,470-row document | OOM after ~12 min | guard fires cleanly in **5.9s** with the path and the limit |

523 existing XML and JSON tests pass unchanged, which is the evidence that output is identical.

---

## The four transferable lessons

### 1. In a fan-out path, per-row overhead is multiplied by the fan-out

The row structure was defensible when written: a dictionary is convenient, and for one row per record the
cost is invisible. It became a 12 GB liability only because `Clone()` sits inside a cross product. **Audit
the allocation profile of whatever object your fan-out clones**, and keep the schema on the collection, not
on the element. This is the same reason columnar layouts exist.

### 2. A resource guard must be expressed in the resource it protects

`MaxRowsPerRecord` is a row count protecting a memory budget. Rows are not a fixed size, so the guard was
decorative: the process died before reaching it. Either measure the real resource, or make the proxy
configurable per workload and document the assumption relating the two. A guard that never fires is worse
than no guard, because it advertises a safety that is not there.

### 3. "It cannot happen, so the constraint will catch it" is not a filter

A second bug in the same episode: three flows relied on the premise that a row with a NULL key is discarded
by the merge. The upsert's INSERT is an anti-join (`WHERE NOT EXISTS` on the keys), so a NULL key matches
nothing, `NOT EXISTS` is true, and the row is **inserted** and then rejected by a NOT NULL constraint. The
production symptom was `Cannot insert the value NULL into column 'BatchDate'`.

Nulling a key does not filter a row, it corrupts its key. **If a row should not be processed, bound the
read.** Do not arrange for it to be rejected downstream and call that filtering.

### 4. Deriving state from emptiness makes cleanup a trigger

The watermark is derived from the target table plus run history. Truncating the landing table as *tidying
up* after a test, and clearing the stale run rows in the same pass, silently converted the next incremental
run into a full-corpus read. Nothing warned, because both conditions are individually legitimate.

**Any state inferred from "the target looks empty" turns routine maintenance into a behavioural change.**
Prefer an explicit, persisted watermark over an inferred one, and if you must infer, log the inference
loudly at the point it changes the plan. This one did log it, which is the only reason the diagnosis took
minutes instead of days.

---

## Still open

- The JSON flattener carries the identical defect (two collections per row rather than three) and has not
  yet been converted.
- Streaming: `FlattenRows` returns `IReadOnlyList<...>`, so the whole product for a record is materialized
  before the first row is yielded. The representation fix reduces the constant; it does not make the record
  size unbounded-safe. A lazy depth-first enumeration would.
- Cross product versus union: independent sibling collections almost never *should* multiply. An
  `explodeMode: union` would emit `|A| + |B|` rows instead of `|A| x |B|`, which is what most documents
  actually mean. It is the right default for new sources but a breaking change for existing ones, so it
  needs to be opt-in.
