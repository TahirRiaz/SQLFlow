# -*- coding: utf-8 -*-
"""Combines the questions and their captured results into the single proof-value fixture the test suite reads.
Values are rendered as strings in the control plane's own formatting so a test can compare without re-deriving
types: that executor returns every cell as a string."""
import json, os, decimal, datetime, re

SP = os.path.dirname(os.path.abspath(__file__))
Q = json.load(open(os.path.join(SP,"questions.json"), encoding="utf-8"))
R = json.load(open(os.path.join(SP,"raw_results.json"), encoding="utf-8"))

# FOR JSON renders a datetime as "2013-11-29T00:00:00"; SqlServerQueryRunner.Render uses the round-trip "O"
# format, which always carries seven fractional digits. Matching it here is what lets a test compare the
# executor's output to these values directly.
_DATETIME = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?$")

# SQL Server's float type is emitted by FOR JSON in scientific notation ("5.275079261999957e+005"), while the
# executor renders the same double with the invariant ToString() that .NET gives it ("527507.9261999957").
_SCIENTIFIC = re.compile(r"^-?\d(\.\d+)?[eE][-+]\d+$")


def _dotnet_double(text):
    """Renders a float the way .NET's invariant double.ToString() does: the shortest text that round-trips to
    the same double. Python's repr uses that same shortest-round-trip algorithm, so it agrees digit for digit,
    including where FOR JSON's fixed 16 significant digits would not."""
    d = float(text)
    if d == int(d) and abs(d) < 1e15:
        return str(int(d))
    return repr(d)


def cell(v, is_float=False):
    """Renders one value exactly as SqlServerQueryRunner.Render does. Numbers arrive here as the exact text
    SQL Server emitted (the capture parses with parse_float=str), so money keeps its trailing zeros and no
    value is ever round-tripped through a float."""
    if v is None:
        return None
    if isinstance(v, bool):
        return "True" if v else "False"
    text = str(v)
    if is_float or _SCIENTIFIC.match(text):
        return _dotnet_double(text)
    m = _DATETIME.match(text)
    if m:
        frac = (m.group(2) or ".")[1:]
        return f"{m.group(1)}.{frac.ljust(7, '0')[:7]}"
    return text

out = []
for q in Q:
    captured = R[q["id"]]
    columns = captured["columns"]
    floats = set(captured.get("floatColumns", []))
    rows = [[cell(r[c], c in floats) for c in columns] for r in captured["rows"]]
    out.append({
        "id": q["id"],
        "shape": q["shape"],
        "question": q["question"],
        "sql": q["sql"],
        "tags": q["tags"],
        "columns": columns,
        "rowCount": len(rows),
        "rows": rows,
    })

fixture = {
    "$comment": ("Proof values for the PowerAI question suite, captured from the AdventureWorksDW sample "
                 "the compose stack restores. Regenerate with tools/powerai-proof/capture.py when the sample "
                 "database changes; never hand-edit a value."),
    "source": {
        "database": "AdventureWorks",
        "datasourceRef": "${env:SQLFLOW_ADVENTUREWORKS_DB}",
        "sample": "AdventureWorksDW2022",
    },
    "questionCount": len(out),
    "questions": out,
}
path = os.path.join(SP, "powerai-proof-values.json")
with open(path, "w", encoding="utf-8") as fh:
    json.dump(fixture, fh, indent=2, ensure_ascii=False)
    fh.write("\n")
print("wrote", path, len(out), "questions",
      sum(x["rowCount"] for x in out), "total rows,", os.path.getsize(path), "bytes")
