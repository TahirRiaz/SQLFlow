# -*- coding: utf-8 -*-
"""Runs every question's SQL against the live AdventureWorks sample and records its result as the proof value.
FOR JSON gives typed, ordered output. A batch that fails is reported rather than recorded as an empty result:
an empty result is only ever accepted when the batch itself ran cleanly."""
import json, subprocess, sys, os

SP = os.path.dirname(os.path.abspath(__file__))
Q = json.load(open(os.path.join(SP, "questions.json"), encoding="utf-8"))

def column_names(sql):
    """Returns a statement's column names in order, for the case where it yields no rows: FOR JSON emits nothing
    at all for an empty result, but the executor still reports the columns, so they must come from elsewhere."""
    probe = ("SELECT name FROM sys.dm_exec_describe_first_result_set(@stmt, NULL, 0) "
             "WHERE is_hidden = 0 ORDER BY column_ordinal")
    path = os.path.join(SP, "_cols.sql")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("SET NOCOUNT ON;\nDECLARE @stmt nvarchar(max) = N'"
                 + sql.replace("'", "''") + "';\n" + probe + ";\n")
    subprocess.run(["docker","cp",path,"sqlflow-mssql-1:/tmp/_cols.sql"], check=True, stdout=subprocess.DEVNULL)
    r = subprocess.run(
        ["docker","exec","sqlflow-mssql-1","bash","-c",
         '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d AdventureWorks '
         '-h -1 -W -i /tmp/_cols.sql'],
        capture_output=True, text=True)
    return [ln.strip() for ln in r.stdout.splitlines() if ln.strip()]


def float_columns(sql):
    """Names a statement's float columns, so they can be re-read in a form that survives the trip."""
    probe = ("SELECT name FROM sys.dm_exec_describe_first_result_set(@stmt, NULL, 0) "
             "WHERE is_hidden = 0 AND system_type_name = 'float' ORDER BY column_ordinal")
    path = os.path.join(SP, "_fl.sql")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("SET NOCOUNT ON;\nDECLARE @stmt nvarchar(max) = N'"
                 + sql.replace("'", "''") + "';\n" + probe + ";\n")
    subprocess.run(["docker","cp",path,"sqlflow-mssql-1:/tmp/_fl.sql"], check=True, stdout=subprocess.DEVNULL)
    r = subprocess.run(
        ["docker","exec","sqlflow-mssql-1","bash","-c",
         '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d AdventureWorks '
         '-h -1 -W -i /tmp/_fl.sql'],
        capture_output=True, text=True)
    return [ln.strip() for ln in r.stdout.splitlines() if ln.strip()]


def float_values(sql, cols):
    """Re-reads a statement's float columns as FORMAT(...,'R') text.

    FOR JSON renders a float with 16 significant digits, which is LOSSY: 40.509272467902996 comes back as
    40.509272467903, a different double, so the true value cannot be recovered afterwards. FORMAT(x,'R') is
    .NET's own round-trip formatter running inside SQL Server, and it is what the executor applies to the
    same value, so these are read separately and spliced over the FOR JSON text.
    """
    if not cols:
        return []
    projected = ", ".join(f"FORMAT([{c}], 'R') AS [{c}]" for c in cols)
    # A CTE may not carry a bare ORDER BY, so the statement is left intact and its float columns are projected
    # from a derived table that keeps the original ordering by selecting through it with OFFSET 0 ROWS.
    wrapped = (f"SELECT {projected} FROM ({sql} OFFSET 0 ROWS) AS capture_src"
               if " ORDER BY " in sql.upper() else
               f"SELECT {projected} FROM ({sql}) AS capture_src")
    path = os.path.join(SP, "_flv.sql")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("SET NOCOUNT ON;\n" + wrapped + " FOR JSON PATH, INCLUDE_NULL_VALUES;\n")
    subprocess.run(["docker","cp",path,"sqlflow-mssql-1:/tmp/_flv.sql"], check=True, stdout=subprocess.DEVNULL)
    r = subprocess.run(
        ["docker","exec","sqlflow-mssql-1","bash","-c",
         '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d AdventureWorks '
         '-y 0 -i /tmp/_flv.sql'],
        capture_output=True, text=True)
    raw = "".join(ln.strip() for ln in r.stdout.splitlines()).strip()
    if not raw or raw.startswith("Msg "):
        return None
    return json.loads(raw, parse_float=str, parse_int=str)


def run_batch(sqls):
    parts = []
    for i, s in enumerate(sqls):
        # Each statement gets its own GO batch so a failure is confined to it and the rest still run.
        parts.append("SET NOCOUNT ON;")
        parts.append(f"PRINT '<<<{i}>>>';")
        # Appended to the statement itself: wrapping it in a subquery would forbid its ORDER BY.
        parts.append(f"{s} FOR JSON PATH, INCLUDE_NULL_VALUES;")
        parts.append("GO")
    path = os.path.join(SP, "_batch.sql")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(parts))
    subprocess.run(["docker","cp",path,"sqlflow-mssql-1:/tmp/_batch.sql"], check=True, stdout=subprocess.DEVNULL)
    r = subprocess.run(
        ["docker","exec","sqlflow-mssql-1","bash","-c",
         '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d AdventureWorks '
         '-y 0 -i /tmp/_batch.sql'],
        capture_output=True, text=True)
    return r.returncode, r.stdout, r.stderr

def parse(out):
    chunks, cur, buf = {}, None, []
    for line in out.splitlines():
        st = line.strip()
        if st.startswith("<<<") and st.endswith(">>>") and st[3:-3].isdigit():
            if cur is not None:
                chunks[cur] = "".join(buf)
            cur, buf = int(st[3:-3]), []
        elif cur is not None:
            buf.append(st)
    if cur is not None:
        chunks[cur] = "".join(buf)
    return chunks

results, errors = {}, {}
BATCH = 25
for start in range(0, len(Q), BATCH):
    grp = Q[start:start+BATCH]
    rc, out, err = run_batch([g["sql"] for g in grp])
    chunks = parse(out)
    for i, g in enumerate(grp):
        if i not in chunks:
            # The batch did not reach this statement (a hard sqlcmd/driver failure). Never record it as empty.
            errors[g["id"]] = f"no output (rc={rc}): {(err or out).strip()[:300]}"
            continue
        raw = chunks[i].strip()
        if raw.startswith("Msg ") or "Level 16" in raw:
            errors[g["id"]] = raw[:300]
            continue
        if not raw:
            # A genuinely empty result set. FOR JSON emitted nothing, so the columns come from the probe.
            results[g["id"]] = {"columns": column_names(g["sql"]), "rows": [],
                                "floatColumns": float_columns(g["sql"])}
            continue
        try:
            rows = json.loads(raw, parse_float=str, parse_int=str)
            fcols = float_columns(g["sql"])
            if fcols:
                exact = float_values(g["sql"], fcols)
                if exact is None or len(exact) != len(rows):
                    errors[g["id"]] = f"could not re-read float columns {fcols} in round-trip form"
                    continue
                for row, ex in zip(rows, exact):
                    for c in fcols:
                        row[c] = ex[c]
            results[g["id"]] = {"columns": list(rows[0].keys()), "rows": rows, "floatColumns": fcols}
        except json.JSONDecodeError as ex:
            errors[g["id"]] = f"unparseable ({ex}): {raw[:300]}"
    sys.stderr.write(f"batch {start//BATCH+1}/{(len(Q)+BATCH-1)//BATCH}\n")

json.dump(results, open(os.path.join(SP,"raw_results.json"),"w"), indent=1, default=str)
json.dump(errors, open(os.path.join(SP,"raw_errors.json"),"w"), indent=1)
print("captured:", len(results), "errors:", len(errors))
for k,v in list(errors.items())[:10]:
    print("ERR", k, v[:220])
