# PowerAI proof values

The question set and stored results behind `tests/SqlFlow.ControlPlane.Tests/PowerAiProofValueTests.cs`.

Every other test around the question surface checks the SHAPE of an exchange: a token is minted, a refusal is a
refusal, the queued payload carries the statement someone was shown. None notices when the NUMBER that comes back
is wrong, because none runs the query against real data. These artifacts close that gap: 300 questions, each with
the SQL that answers it and the result that SQL produced against the AdventureWorksDW sample, captured cell for
cell in the executor's own rendering.

## The files

| File | What it is |
| --- | --- |
| `generate_questions.py` | Builds `questions.json`: the 300 questions and their SQL. The only place a question is authored. |
| `questions.json` | The generated question set. Regenerate rather than hand-edit. |
| `capture.py` | Runs every question against the live sample and writes `raw_results.json`. |
| `build_fixture.py` | Combines the two into `powerai-proof-values.json`, the fixture the tests read. |

The fixture lives at `tests/SqlFlow.ControlPlane.Tests/Fixtures/powerai-proof-values.json`.

## Answer shapes

Each question declares the shape of its answer, and the shape describes what the query actually returns rather
than what it was meant to return:

- **scalar**: one row, one column. The answer a person reads as a sentence.
- **multivalue**: one row, several named columns. Read across (sales, cost, and profit together).
- **dataset**: many rows. Read down, so row ORDER is part of the answer and every dataset question orders
  explicitly.

## Regenerating

Needs the compose stack up, with the AdventureWorks sample restored (`deploy/compose`, `docker compose up -d`).
The capture talks to the `sqlflow-mssql-1` container directly and never materialises its password.

```bash
python3 tools/powerai-proof/generate_questions.py   # only when questions change
python3 tools/powerai-proof/capture.py              # runs all 300 against the sample
python3 tools/powerai-proof/build_fixture.py
cp tools/powerai-proof/powerai-proof-values.json \
   tests/SqlFlow.ControlPlane.Tests/Fixtures/powerai-proof-values.json
```

Regenerate only once a change in the values is understood and intended. A failing proof value is the suite doing
its job: it means the read path or the sample moved, and the right response is to find out which before replacing
the evidence.

## Two things the capture has to get exactly right

Both were found by the tests failing, and both are why the capture is more than "run the query and save the rows".

- **`float` precision.** `FOR JSON` renders a float with 16 significant digits, which is lossy:
  `40.509272467902996` comes back as `40.509272467903`, a genuinely different double, and the true value cannot
  be recovered afterwards. Float columns are therefore re-read through `FORMAT(x, 'R')`, which is .NET's own
  round-trip formatter running inside SQL Server, matching what `SqlServerQueryRunner.Render` produces.
- **Empty results.** `FOR JSON` emits nothing at all for a result with no rows, so the column names come from
  `sys.dm_exec_describe_first_result_set` instead. The executor still reports columns for an empty result, and a
  question whose answer is legitimately nothing is one of the cases most easily reported wrongly.

`money` needs no special handling but is easy to break: it keeps its trailing zeros (`4607537.9350`), so the
capture parses JSON with `parse_float=str` and never routes a value through a Python float.
