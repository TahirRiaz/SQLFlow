# pbix-extract

Reads a Power BI report's semantic model and writes it as a SQLFlow YAML specification: the
tables and columns that exist, what each measure computes (its DAX), which columns are themselves
computed, how the tables relate, and where each table's data comes from.

The point is to state what a model MEANS, in a form a SQL author, or a language model asked to
write SQL, can work from. A warehouse schema says what columns exist; it does not say that
"Sales Amount by Due Date" means `CALCULATE(SUM(Sales[Sales Amount]), USERELATIONSHIP(...))`, or
that `Sales.DueDateKey -> Date.DateKey` is a real relationship that is deliberately inactive.
That knowledge lives only in the report, and this extracts it.

## Why this is a standalone binary

A `.pbix` report's `DataModel` part is a compressed Analysis Services backup: an XPress9 block
stream wrapping a container whose directory is UTF-16 XML, wrapping a SQLite database that holds
the model's metadata. Reading it means running a decompressor over a binary file that, from
SQLFlow's point of view, is untrusted input.

So this is a separate executable, built outside `SqlFlow.sln`, run offline against files already
on disk. Nothing here links into the SQLFlow control plane, and the only thing that crosses back
is reviewed YAML text. A malformed or hostile report can at worst crash this tool.

This tool also reads the report's *visual* layer (pages, visuals, which fields sit in which role),
which is plain JSON and carries no native-code risk. Power BI Desktop writes that layer in either of
two shapes, and both are read (see "Report formats" below). Both halves
live here so there is ONE reader of a `.pbix` rather than two implementations to keep in step: the
visual layer tracks the questions a report asks, and the model describes what those questions are
asked against. SQLFlow consumes this tool's YAML output rather than parsing `.pbix` itself.

Keeping extraction out of the control plane is deliberate and is the security posture: a `.pbix` is
attacker-influenceable input fed to a memory-unsafe decoder, so it is parsed on a developer machine
or a build agent, never inside the long-running server process that holds catalog credentials and
reaches the warehouse.

## Where it runs

- **Where the reports live** (a developer machine, a build agent): `sqlflow db sync` runs it for every `.pbix` a
  subscriber declares and keeps what it extracted in the semantic layer, and `sqlflow powerbi extract` writes the
  specification to a `<report>.pbix.yaml` a subscriber can declare instead of the report.
- **In its own container** (`Dockerfile.pbix-extractor`, `src/SqlFlow.PbixExtractor`): a small HTTP service the
  control plane forwards uploaded reports to. The service holds no credentials, and the control plane validates
  what it answers before storing it. This is how a report uploaded in the GUI, or extracted by the CLI on a machine
  without this tool, is read.

It never runs inside the control plane itself.

## Build

Needs a C11 compiler and the development packages for libzip, expat, and SQLite (with
`sqlite3_deserialize`, i.e. SQLite 3.23 or newer):

```sh
make
```

The binary lands at `build/pbix-extract`. `make clean` removes the build tree.

`make test` builds and runs the test suite, which drives the reader and the SQL renderer over
synthetic `.pbix` fixtures built in-process. `make test-asan` runs the same suite under
AddressSanitizer and UndefinedBehaviorSanitizer, including the vendored decoder: these parsers
handle untrusted input, so a leak or an out-of-bounds read fails the run rather than passing
quietly.

## Usage

```sh
build/pbix-extract "reports/AdventureWorks Sales.pbix"                 # to standard output
build/pbix-extract report.pbix --name Sales_Report --out spec.yaml     # to a file
build/pbix-extract report.pbix --report-file "team/sales.pbix"         # label its pages
```

`--name` sets the subscriber key in the emitted YAML (default: the file's base name with spaces
replaced by underscores). `--out` writes to a file instead of standard output.

`--report-file` labels every extracted page with the report it came from (default: the file's
name). Pass the path relative to the subscriber's declared directory when several reports are
extracted under one subscriber, so that two reports built from the same template, each with a
"Page 1", stay distinguishable.

The output is a fragment, not a complete `subscribers.yaml`. Merging it into the estate means
adding the things the `.pbix` does not itself declare: `owner:`, `description:`, `url:`, and the
`server:` connection alias the report reads through.

## What it reads, and what it does not

Read from the model's embedded metadata:

- **tables / columns** with the model's declared data type for each
- **measures**: name, home table, DAX expression, description
- **calculatedColumns**: name, home table, DAX expression
- **relationships**: endpoints, cardinality (`M:1`, `1:1`, …), and whether the relationship is
  active
- **tableSources**: each table's Power Query (M) expression

Power BI's auto-generated date-hierarchy tables (the `LocalDateTable_*` /
`DateTableTemplate_*` scaffolding, flagged in the model as system objects) are excluded: nobody
authored them, and they would bury the real model in noise.

Read from the report's visual layer:

- **pages**: display name, internal name, position, and the report file they came from
- **visuals**: chart type, authored title, position on the page
- **fields**: the table and column/measure each one names, and its ROLE (`Category`, `Y`, `Rows`,
  `Values`, `Size`, and so on), which is the one fact SQL alone cannot express: it is the difference
  between "sales by month" and "months by sales"
- **sql**: each visual's question rendered as one T-SQL `SELECT`, with every filter that applies to
  it (its own, and the page's) folded into the `WHERE` clause

A visual that projects no field is decoration (a textbox, a shape, an image), asks no question, and
is deliberately not recorded. A visual whose query or filter uses an expression this tool cannot
represent is DROPPED rather than recorded without it, and the reason appears under
`reportWarnings:`. That refusal is the point: a query missing part of its filter is broader than
the question actually on the page, and silently widening a question is how a wrong answer gets
trusted.

Not read: row-level security, perspectives, translations, KPIs, visual styling (colors, positions,
sizes), and the column data itself. A model whose inner files are XPress8-compressed
(`ApplyCompression`) is reported as unsupported rather than silently returning nothing.

## Report formats

Power BI Desktop writes the visual layer in one of two shapes, and which one a file uses depends on
the Desktop version that last saved it. Both are read, and which is present is detected from the
file rather than configured:

- **`Report/Layout`**, the older single part: UTF-16LE JSON holding every page and visual, with a
  visual's `config` and `filters` nested as JSON *strings* inside that JSON, and each field behind a
  `queryRef` pointing into a separate `prototypeQuery`.
- **`Report/definition/...`**, the newer split form: plain UTF-8 JSON spread over one document per
  page (`pages/<page>/page.json`) and per visual (`pages/<page>/visuals/<id>/visual.json`), ordered
  by `pages/pages.json`. Field expressions sit INLINE under their role, filters are a native
  `filterConfig.filters` array, and sort direction is spelled out rather than numeric.

The expression vocabulary inside both (`Column`, `Measure`, `HierarchyLevel`, `Aggregation`, `In`,
`Comparison`, ...) is identical, so one set of expression, filter and SQL-rendering code serves
both; only the shape around those expressions differs. One consequence is worth knowing: the split
form states no query-level `FROM`, because its references name their entity directly instead of
binding an alias, so the rendered `FROM` is reconstructed from the entities a visual's fields
actually name.

A file with neither part is reported as having no readable visual layer, which is distinct from a
file that has one holding no pages.

## Schema variation

Power BI's embedded metadata schema changes between versions: columns are renamed and added. Every
query this tool runs is built against the columns the database actually has, probed at runtime:
for example a relationship's endpoints are `FromColumnID` on current models and
`FromEndColumnID` on older ones.

Where a needed table or column is missing in both spellings, the tool stops and says which one it
could not find, rather than returning an empty section. An empty model and an unreadable one mean
very different things, and conflating them is how a wrong answer gets trusted.

This has been verified against one real report. Other Power BI versions may use schema shapes the
probes do not yet cover; the failure mode is a stated error naming the unexpected column, which is
enough to extend the probe.

## Vendored code

`vendor/xpress9/` is the XPress9 reference decoder, MIT licensed (see
`vendor/xpress9/LICENSE`), taken from [xpress9-python](https://github.com/Hugoberry/xpress9-python),
which wraps Microsoft's own reference implementation. Only the decode path is vendored; the
encoder translation units are deliberately absent, since a report is only ever read. The sources
are compiled with warnings suppressed, because they are upstream's code rather than this
project's (this project's own sources build clean under `-Wall -Wextra -Wpedantic`).

One deliberate divergence from upstream, marked `LOCAL FIX` in the source:
`Xpress9DecLz77.c` shifted its sliding window with `memcpy` on overlapping ranges (the source and
destination are the same buffer, and they overlap whenever the shift distance is less than the
window size, which is the ordinary case). That is undefined behavior, and AddressSanitizer flags
it as `memcpy-param-overlap`. It is now `memmove`. The change produces byte-identical output on
the reports tested, but the original relied on a particular libc's forward-copy behavior, and a
silently corrupted decode window would surface much later as plausible-looking nonsense.

The tool is checked under AddressSanitizer and UndefinedBehaviorSanitizer against a real report;
both are silent, with no leaks. Since this code parses a binary file that should be treated as
untrusted, that check is part of the build-and-verify routine rather than a one-off:

```sh
# from tools/pbix-extract
mkdir -p build-asan
for f in src/*.c; do
  cc -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -std=c11 \
     -D_POSIX_C_SOURCE=200809L -Isrc -Ivendor/xpress9/include \
     $(pkg-config --cflags libzip expat sqlite3) -c "$f" -o "build-asan/$(basename "$f" .c).o"
done
for f in vendor/xpress9/src/*.c; do
  cc -O1 -g -fsanitize=address,undefined -fno-omit-frame-pointer -std=c11 -DBUILD_STATIC \
     -Ivendor/xpress9/include -w -c "$f" -o "build-asan/$(basename "$f" .c).o"
done
cc -fsanitize=address,undefined build-asan/*.o -o build-asan/pbix-extract \
   $(pkg-config --libs libzip expat sqlite3)
ASAN_OPTIONS=detect_leaks=1 ./build-asan/pbix-extract report.pbix > /dev/null
```
