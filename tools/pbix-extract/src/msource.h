/*
 * Resolves a Power Query (M) table expression to the physical warehouse object it loads from.
 *
 * A report's visuals and DAX only ever name the MODEL's table ("Sales"); the M expression behind
 * that table is the only record of where the data actually came from. Without this, a consumption
 * edge extracted from a report lands on a name-only node that never unifies with the fully
 * qualified node an ingestion flow writes, so "what reads this table" cannot see the report.
 *
 * This is a PATTERN MATCHER over a short, closed list of known source shapes, deliberately not an M
 * parser. M is a full functional language; parsing it would be a large, permanent maintenance
 * surface for a feature whose payoff is a name-to-name mapping. The recognized shape is:
 *
 *     Sql.Database("server", "database")          -- or Sql.Databases(...) indexed by name
 *     ... {[Schema="arc", Item="Sales"]}[Data]    -- anywhere later in the let chain
 *
 * Everything else (Excel.Workbook, Csv.Document, Web.Contents, a native [Query="..."] source, a
 * merged query spanning sources) is UNRESOLVED, and reported as such rather than guessed at. An
 * unresolved table is honest; a wrongly resolved one silently corrupts the lineage graph, so every
 * ambiguity here fails closed.
 *
 * The server string is returned VERBATIM from the M literal and is explicitly a candidate identity,
 * not an estate identity: how an M connection string maps onto a declared connection reference is a
 * question only the estate's own configuration can answer, so the caller decides that.
 */

#ifndef PBIX_MSOURCE_H
#define PBIX_MSOURCE_H

/*
 * What one M expression resolved to. Every field is either a fresh allocation owned by the caller
 * or NULL. `resolved` is 1 only when server, database, schema and item were all found.
 *
 * When `resolved` is 0, `unresolved_shape` names the source function that defeated resolution
 * ("Excel.Workbook", "Web.Contents", or "unknown"), for a warning that tells a person WHY a table
 * did not resolve rather than only that it did not.
 */
typedef struct {
    int resolved;
    char *server;   /* the M literal, verbatim; a candidate identity, not an estate one */
    char *database;
    char *schema;
    char *item;
    char *unresolved_shape;
} MSourceResolution;

/*
 * Attempts to resolve `expression`. Never fails destructively: an unparseable, NULL, or
 * unrecognized expression yields a zeroed result with `resolved` 0, optionally naming the shape.
 * Returns 0 on success (resolved or not) and -1 only on allocation failure.
 *
 * The caller frees the result with msource_free.
 */
int msource_resolve(const char *expression, MSourceResolution *out);

/* Frees the strings owned by a resolution and zeroes it. Safe on a zeroed or NULL struct. */
void msource_free(MSourceResolution *resolution);

#endif /* PBIX_MSOURCE_H */
