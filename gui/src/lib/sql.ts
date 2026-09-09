import { format as formatSql } from "sql-formatter";

/**
 * Pretty-prints captured T-SQL for display. The control plane executes against SQL Server, so captured statements
 * are Transact-SQL and are stored as single-line blobs (UPDATE/MERGE with long HASHBYTES/CONCAT expressions).
 * Formatting is best-effort: any input the parser rejects is returned unchanged so the caller always shows the real
 * SQL rather than an error.
 */
export function prettyPrintSql(sql: string): string {
  try {
    return formatSql(sql, {
      language: "transactsql",
      keywordCase: "upper",
      tabWidth: 2,
      linesBetweenQueries: 1,
    });
  } catch {
    return sql;
  }
}
