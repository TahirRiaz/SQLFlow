#include "metadata.h"

#include "redact.h"

#include <sqlite3.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void set_error(char *error, size_t error_size, const char *message)
{
    if (error != NULL && error_size > 0) {
        snprintf(error, error_size, "%s", message);
    }
}

static void set_errorf(char *error, size_t error_size, const char *format, ...)
{
    va_list args;

    if (error == NULL || error_size == 0) {
        return;
    }
    va_start(args, format);
    vsnprintf(error, error_size, format, args);
    va_end(args);
}

/* Duplicates a query result column, yielding NULL for SQL NULL and for the empty string. */
static char *dup_column(sqlite3_stmt *statement, int index)
{
    const unsigned char *text = sqlite3_column_text(statement, index);

    if (text == NULL || text[0] == '\0') {
        return NULL;
    }
    return strdup((const char *)text);
}

/* ---- Schema probing ---------------------------------------------------------------------- */

/*
 * The embedded schema is not stable across Power BI versions: columns get renamed and added as
 * the Analysis Services engine evolves. Every query below is therefore built against what the
 * database ACTUALLY has, probed here, rather than against one assumed shape. A model whose schema
 * lacks an expected table is reported as an error naming that table, not silently returned empty:
 * an empty specification and an unreadable one mean very different things to whoever reads the
 * output, and conflating them is how a wrong answer gets trusted.
 */
static int table_exists(sqlite3 *db, const char *table)
{
    sqlite3_stmt *statement;
    int exists = 0;

    if (sqlite3_prepare_v2(db,
            "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = ? LIMIT 1",
            -1, &statement, NULL) != SQLITE_OK) {
        return 0;
    }
    sqlite3_bind_text(statement, 1, table, -1, SQLITE_STATIC);
    if (sqlite3_step(statement) == SQLITE_ROW) {
        exists = 1;
    }
    sqlite3_finalize(statement);
    return exists;
}

static int column_exists(sqlite3 *db, const char *table, const char *column)
{
    sqlite3_stmt *statement;
    char sql[256];
    int exists = 0;

    snprintf(sql, sizeof(sql), "PRAGMA table_info(\"%s\")", table);
    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        return 0;
    }
    while (sqlite3_step(statement) == SQLITE_ROW) {
        const unsigned char *name = sqlite3_column_text(statement, 1);

        if (name != NULL && strcmp((const char *)name, column) == 0) {
            exists = 1;
            break;
        }
    }
    sqlite3_finalize(statement);
    return exists;
}

/*
 * Picks whichever of two column spellings the schema has. Older models name a relationship's
 * endpoints with an "End" infix (FromColumnID -> FromEndColumnID); newer ones do not. Returns
 * NULL when neither exists, which the caller turns into a stated error.
 */
static const char *pick_column(sqlite3 *db, const char *table, const char *modern, const char *legacy)
{
    if (column_exists(db, table, modern)) {
        return modern;
    }
    if (column_exists(db, table, legacy)) {
        return legacy;
    }
    return NULL;
}

/* ---- Generic row collection -------------------------------------------------------------- */

/* Grows an array of `element_size` records by one, returning the new slot or NULL. */
static void *push_row(void **array, size_t *count, size_t element_size)
{
    void *grown = realloc(*array, (*count + 1) * element_size);

    if (grown == NULL) {
        return NULL;
    }
    *array = grown;
    memset((char *)grown + (*count * element_size), 0, element_size);
    return (char *)grown + ((*count)++ * element_size);
}

/* ---- The five readers -------------------------------------------------------------------- */

static int read_measures(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char sql[1024];
    const char *description;
    int status;

    if (!table_exists(db, "Measure") || !table_exists(db, "Table")) {
        set_error(error, error_size,
            "the model's metadata has no 'Measure' or 'Table' table; its schema is not one this "
            "tool recognizes");
        return -1;
    }

    /* Description is absent on older schemas; select a literal NULL instead so the column
     * positions below stay fixed. */
    description = column_exists(db, "Measure", "Description") ? "m.Description" : "NULL";

    snprintf(sql, sizeof(sql),
        "SELECT t.Name, m.Name, m.Expression, %s "
        "FROM Measure m JOIN \"Table\" t ON m.TableID = t.ID "
        "ORDER BY t.Name, m.Name",
        description);

    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's measures could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        Measure *measure = (Measure *)push_row(
            (void **)&spec->measures, &spec->measure_count, sizeof(*spec->measures));

        if (measure == NULL) {
            set_error(error, error_size, "out of memory reading the model's measures");
            sqlite3_finalize(statement);
            return -1;
        }

        measure->table = dup_column(statement, 0);
        measure->name = dup_column(statement, 1);
        measure->expression = dup_column(statement, 2);
        measure->description = dup_column(statement, 3);
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's measures could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

static int read_calculated_columns(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char sql[1024];
    const char *type_column;
    int status;

    if (!table_exists(db, "Column") || !table_exists(db, "Table")) {
        set_error(error, error_size,
            "the model's metadata has no 'Column' or 'Table' table; its schema is not one this "
            "tool recognizes");
        return -1;
    }

    /* A calculated column is column type 2. The discriminator column was renamed between
     * schema versions. */
    type_column = pick_column(db, "Column", "Type", "ColumnType");
    if (type_column == NULL) {
        set_error(error, error_size,
            "the model's 'Column' table has neither a 'Type' nor a 'ColumnType' column, so "
            "calculated columns cannot be told apart from stored ones");
        return -1;
    }

    /* Power BI generates a hidden calendar table per date column (DateTableTemplate_*,
     * LocalDateTable_*), each carrying the same six boilerplate calculated columns. They are
     * flagged as system objects; without this filter they outnumber a report's real calculated
     * columns many times over and bury them. */
    snprintf(sql, sizeof(sql),
        "SELECT t.Name, c.ExplicitName, c.Expression "
        "FROM \"Column\" c JOIN \"Table\" t ON c.TableID = t.ID "
        "WHERE c.%s = 2 AND c.Expression IS NOT NULL AND c.Expression <> '' %s "
        "ORDER BY t.Name, c.ExplicitName",
        type_column,
        column_exists(db, "Table", "SystemFlags") ? "AND COALESCE(t.SystemFlags, 0) = 0" : "");

    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's calculated columns could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        CalculatedColumn *column = (CalculatedColumn *)push_row(
            (void **)&spec->calculated_columns, &spec->calculated_column_count,
            sizeof(*spec->calculated_columns));

        if (column == NULL) {
            set_error(error, error_size, "out of memory reading the model's calculated columns");
            sqlite3_finalize(statement);
            return -1;
        }

        column->table = dup_column(statement, 0);
        column->name = dup_column(statement, 1);
        column->expression = dup_column(statement, 2);
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's calculated columns could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

static int read_relationships(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char sql[2048];
    const char *from_table;
    const char *from_column;
    const char *to_table;
    const char *to_column;
    const char *from_cardinality;
    const char *to_cardinality;
    const char *is_active;
    char cardinality[256];
    char where[256];
    int status;

    if (!table_exists(db, "Relationship")) {
        /* A single-table model has no relationships and no Relationship table. That is a
         * complete, valid specification, not a failure. */
        return 0;
    }

    from_table = pick_column(db, "Relationship", "FromTableID", "FromEndTableID");
    from_column = pick_column(db, "Relationship", "FromColumnID", "FromEndColumnID");
    to_table = pick_column(db, "Relationship", "ToTableID", "ToEndTableID");
    to_column = pick_column(db, "Relationship", "ToColumnID", "ToEndColumnID");
    from_cardinality = pick_column(db, "Relationship", "FromCardinality", "FromEndCardinality");
    to_cardinality = pick_column(db, "Relationship", "ToCardinality", "ToEndCardinality");

    if (from_table == NULL || from_column == NULL || to_table == NULL || to_column == NULL) {
        set_error(error, error_size,
            "the model's 'Relationship' table does not name its endpoints in either the modern "
            "(FromColumnID) or legacy (FromEndColumnID) spelling; its schema is not one this tool "
            "recognizes");
        return -1;
    }

    /*
     * Cardinality codes: 2 is "many", anything else is "one". When the schema does not record
     * cardinality at all, the relationship is still real and worth reporting, so the cardinality
     * is emitted as unknown rather than dropping the row.
     */
    is_active = column_exists(db, "Relationship", "IsActive") ? "rel.IsActive" : "1";

    /*
     * Cardinality is a pair of per-endpoint codes rather than one column, so the CASE pair is
     * built from the probed column names first and substituted as a unit. Without both codes the
     * relationship is still reported, with its cardinality left unstated.
     */
    if (from_cardinality != NULL && to_cardinality != NULL) {
        snprintf(cardinality, sizeof(cardinality),
            "(CASE WHEN rel.%s = 2 THEN 'M' ELSE '1' END) || ':' || "
            "(CASE WHEN rel.%s = 2 THEN 'M' ELSE '1' END)",
            from_cardinality, to_cardinality);
    } else {
        snprintf(cardinality, sizeof(cardinality), "NULL");
    }

    /* Auto-generated date hierarchy tables carry a non-zero SystemFlags; they are Power BI's
     * own scaffolding, not part of the model a person authored. */
    if (column_exists(db, "Table", "SystemFlags")) {
        snprintf(where, sizeof(where),
            "WHERE COALESCE(ft.SystemFlags, 0) = 0 AND COALESCE(tt.SystemFlags, 0) = 0");
    } else {
        where[0] = '\0';
    }

    snprintf(sql, sizeof(sql),
        "SELECT ft.Name, fc.ExplicitName, tt.Name, tc.ExplicitName, "
        "       %s, %s "
        "FROM Relationship rel "
        "  LEFT JOIN \"Table\" ft ON rel.%s = ft.ID "
        "  LEFT JOIN \"Column\" fc ON rel.%s = fc.ID "
        "  LEFT JOIN \"Table\" tt ON rel.%s = tt.ID "
        "  LEFT JOIN \"Column\" tc ON rel.%s = tc.ID "
        "%s "
        "ORDER BY ft.Name, fc.ExplicitName",
        cardinality, is_active,
        from_table, from_column, to_table, to_column, where);

    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's relationships could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        Relationship *relationship = (Relationship *)push_row(
            (void **)&spec->relationships, &spec->relationship_count,
            sizeof(*spec->relationships));

        if (relationship == NULL) {
            set_error(error, error_size, "out of memory reading the model's relationships");
            sqlite3_finalize(statement);
            return -1;
        }

        relationship->from_table = dup_column(statement, 0);
        relationship->from_column = dup_column(statement, 1);
        relationship->to_table = dup_column(statement, 2);
        relationship->to_column = dup_column(statement, 3);
        relationship->cardinality = dup_column(statement, 4);
        relationship->active = sqlite3_column_int(statement, 5) != 0;
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's relationships could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

static int read_columns(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char sql[1024];
    const char *system_flags_filter;
    int status;

    if (!table_exists(db, "Column") || !table_exists(db, "Table")) {
        set_error(error, error_size,
            "the model's metadata has no 'Column' or 'Table' table; its schema is not one this "
            "tool recognizes");
        return -1;
    }

    system_flags_filter = column_exists(db, "Table", "SystemFlags")
        ? "AND COALESCE(t.SystemFlags, 0) = 0" : "";

    /*
     * ExplicitDataType is the model's declared type; InferredDataType is what the engine worked
     * out for a calculated column. Preferring the explicit one and falling back keeps a type on
     * every row, which is what a SQL author needs in order to cast and compare correctly.
     *
     * The type is stored as an AMO numeric code, which says nothing to a reader; it is mapped to
     * the engine's own name for the type so the emitted specification states "string" rather
     * than "2". An unmapped code is emitted as its number rather than guessed at.
     *
     * Every VertiPaq table also carries an internal "RowNumber-<GUID>" column: a surrogate the
     * engine adds for its own indexing, not a field of the model. It is excluded, since it
     * exists in no query a person would write.
     */
    snprintf(sql, sizeof(sql),
        "SELECT t.Name, c.ExplicitName, "
        /* Code 1 is 'automatic': the engine left the type to be derived, which is what a
         * calculated column whose expression returns another column's value gets. Stating it
         * beats emitting a bare number, and beats guessing a concrete type the model never
         * committed to. */
        "       CASE COALESCE(%s, %s) "
        "         WHEN 1 THEN 'automatic' "
        "         WHEN 2 THEN 'string' WHEN 6 THEN 'int64' WHEN 8 THEN 'double' "
        "         WHEN 9 THEN 'dateTime' WHEN 10 THEN 'decimal' WHEN 11 THEN 'boolean' "
        "         WHEN 17 THEN 'binary' "
        "         ELSE CAST(COALESCE(%s, %s) AS TEXT) END "
        "FROM \"Column\" c JOIN \"Table\" t ON c.TableID = t.ID "
        "WHERE c.ExplicitName NOT LIKE 'RowNumber-%%' %s "
        "ORDER BY t.Name, c.ExplicitName",
        column_exists(db, "Column", "ExplicitDataType") ? "c.ExplicitDataType" : "NULL",
        column_exists(db, "Column", "InferredDataType") ? "c.InferredDataType" : "NULL",
        column_exists(db, "Column", "ExplicitDataType") ? "c.ExplicitDataType" : "NULL",
        column_exists(db, "Column", "InferredDataType") ? "c.InferredDataType" : "NULL",
        system_flags_filter);

    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's columns could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        Column *column = (Column *)push_row(
            (void **)&spec->columns, &spec->column_count, sizeof(*spec->columns));

        if (column == NULL) {
            set_error(error, error_size, "out of memory reading the model's columns");
            sqlite3_finalize(statement);
            return -1;
        }

        column->table = dup_column(statement, 0);
        column->column = dup_column(statement, 1);
        column->data_type = dup_column(statement, 2);
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's columns could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

static int read_sources(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char sql[1024];
    char *raw;
    int status;

    if (!table_exists(db, "Partition") || !table_exists(db, "Table")) {
        /* No partitions recorded means no Power Query to report. */
        return 0;
    }

    /*
     * Partition type 4 is an M (Power Query) partition. Without a Type column the schema cannot
     * distinguish an M partition from a calculated one, so no source is reported rather than
     * reporting a calculated table's DAX as if it were M.
     */
    if (!column_exists(db, "Partition", "Type")) {
        return 0;
    }

    snprintf(sql, sizeof(sql),
        "SELECT t.Name, p.QueryDefinition "
        "FROM Partition p JOIN \"Table\" t ON t.ID = p.TableID "
        "WHERE p.Type = 4 AND p.QueryDefinition IS NOT NULL AND p.QueryDefinition <> '' "
        "ORDER BY t.Name");

    if (sqlite3_prepare_v2(db, sql, -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's table sources could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        TableSource *source = (TableSource *)push_row(
            (void **)&spec->sources, &spec->source_count, sizeof(*spec->sources));

        if (source == NULL) {
            set_error(error, error_size, "out of memory reading the model's table sources");
            sqlite3_finalize(statement);
            return -1;
        }

        source->table = dup_column(statement, 0);

        /*
         * The M expression is redacted as it is read, so no later step can emit the author's
         * local path by forgetting to. See redact.h for what is removed and what is kept.
         */
        raw = dup_column(statement, 1);
        if (raw != NULL) {
            source->expression = redact_m_expression(raw);
            free(raw);
            if (source->expression == NULL) {
                set_error(error, error_size, "out of memory reading the model's table sources");
                sqlite3_finalize(statement);
                return -1;
            }
        }
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's table sources could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

/*
 * Reads the model's shared expressions: Power Query queries kept in the model but not loaded as
 * tables. A table's M merges them in by name (Table.NestedJoin(..., DimGeography, ...)), so a
 * consumer mapping a model column back to its source needs their text too. Kind 0 is an M
 * expression; parameters and other kinds are skipped. A schema without the table, or without a
 * Kind column to tell M from anything else, simply has none to report.
 */
static int read_expressions(sqlite3 *db, ModelSpec *spec, char *error, size_t error_size)
{
    sqlite3_stmt *statement;
    char *raw;
    int status;

    if (!table_exists(db, "Expression") || !column_exists(db, "Expression", "Kind")
        || !column_exists(db, "Expression", "Name") || !column_exists(db, "Expression", "Expression")) {
        return 0;
    }

    if (sqlite3_prepare_v2(db,
            "SELECT Name, Expression FROM Expression "
            "WHERE Kind = 0 AND Name IS NOT NULL AND Expression IS NOT NULL AND Expression <> '' "
            "ORDER BY Name",
            -1, &statement, NULL) != SQLITE_OK) {
        set_errorf(error, error_size, "the model's shared expressions could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    while ((status = sqlite3_step(statement)) == SQLITE_ROW) {
        SharedExpression *expression = (SharedExpression *)push_row(
            (void **)&spec->expressions, &spec->expression_count, sizeof(*spec->expressions));

        if (expression == NULL) {
            set_error(error, error_size, "out of memory reading the model's shared expressions");
            sqlite3_finalize(statement);
            return -1;
        }

        expression->name = dup_column(statement, 0);

        /* Redacted as it is read, exactly like a table's own M. */
        raw = dup_column(statement, 1);
        if (raw != NULL) {
            expression->expression = redact_m_expression(raw);
            free(raw);
            if (expression->expression == NULL) {
                set_error(error, error_size, "out of memory reading the model's shared expressions");
                sqlite3_finalize(statement);
                return -1;
            }
        }
    }

    sqlite3_finalize(statement);

    if (status != SQLITE_DONE) {
        set_errorf(error, error_size, "the model's shared expressions could not be read: %s",
            sqlite3_errmsg(db));
        return -1;
    }

    return 0;
}

/* ---- Public interface -------------------------------------------------------------------- */

int model_spec_read(const DataModel *model, ModelSpec *spec, char *error, size_t error_size)
{
    const unsigned char *sqlite_bytes;
    size_t sqlite_size;
    unsigned char *owned;
    sqlite3 *db = NULL;
    int status = -1;

    memset(spec, 0, sizeof(*spec));

    if (model->apply_compression) {
        /*
         * With ApplyCompression set, each inner file is separately XPress8-compressed, a
         * different codec from the XPress9 used for the outer stream. Reporting this plainly
         * beats handing back an empty specification from bytes that were never decompressed.
         */
        set_error(error, error_size,
            "this data model marks its inner files as compressed (ApplyCompression), which uses "
            "the XPress8 codec. Only uncompressed inner files are supported; re-saving the report "
            "from Power BI Desktop produces a model this tool can read.");
        return -1;
    }

    if (data_model_find(model, "metadata.sqlitedb", &sqlite_bytes, &sqlite_size,
            error, error_size) != 0) {
        return -1;
    }

    /*
     * sqlite3_deserialize takes ownership of a buffer it can free with sqlite3_free, so the
     * slice is copied out of the image into SQLite's own allocator rather than handing it a
     * pointer into a buffer this process owns.
     */
    owned = (unsigned char *)sqlite3_malloc64((sqlite3_uint64)sqlite_size);
    if (owned == NULL) {
        set_error(error, error_size, "out of memory copying the model's metadata database");
        return -1;
    }
    memcpy(owned, sqlite_bytes, sqlite_size);

    if (sqlite3_open_v2(":memory:", &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, NULL)
        != SQLITE_OK) {
        set_errorf(error, error_size, "an in-memory database could not be opened: %s",
            db != NULL ? sqlite3_errmsg(db) : "unknown error");
        sqlite3_free(owned);
        sqlite3_close(db);
        return -1;
    }

    if (sqlite3_deserialize(db, "main", owned, (sqlite3_int64)sqlite_size,
            (sqlite3_int64)sqlite_size, SQLITE_DESERIALIZE_FREEONCLOSE | SQLITE_DESERIALIZE_RESIZEABLE)
        != SQLITE_OK) {
        set_errorf(error, error_size,
            "the model's metadata database could not be opened: %s. The file may be from an "
            "unsupported Power BI version.",
            sqlite3_errmsg(db));
        sqlite3_close(db);
        return -1;
    }

    if (read_measures(db, spec, error, error_size) != 0
        || read_calculated_columns(db, spec, error, error_size) != 0
        || read_relationships(db, spec, error, error_size) != 0
        || read_columns(db, spec, error, error_size) != 0
        || read_sources(db, spec, error, error_size) != 0
        || read_expressions(db, spec, error, error_size) != 0) {
        goto done;
    }

    status = 0;

done:
    sqlite3_close(db);
    if (status != 0) {
        model_spec_free(spec);
    }
    return status;
}

void model_spec_free(ModelSpec *spec)
{
    size_t i;

    for (i = 0; i < spec->measure_count; i++) {
        free(spec->measures[i].table);
        free(spec->measures[i].name);
        free(spec->measures[i].expression);
        free(spec->measures[i].description);
    }
    free(spec->measures);

    for (i = 0; i < spec->calculated_column_count; i++) {
        free(spec->calculated_columns[i].table);
        free(spec->calculated_columns[i].name);
        free(spec->calculated_columns[i].expression);
    }
    free(spec->calculated_columns);

    for (i = 0; i < spec->relationship_count; i++) {
        free(spec->relationships[i].from_table);
        free(spec->relationships[i].from_column);
        free(spec->relationships[i].to_table);
        free(spec->relationships[i].to_column);
        free(spec->relationships[i].cardinality);
    }
    free(spec->relationships);

    for (i = 0; i < spec->column_count; i++) {
        free(spec->columns[i].table);
        free(spec->columns[i].column);
        free(spec->columns[i].data_type);
    }
    free(spec->columns);

    for (i = 0; i < spec->source_count; i++) {
        free(spec->sources[i].table);
        free(spec->sources[i].expression);
    }
    free(spec->sources);

    for (i = 0; i < spec->expression_count; i++) {
        free(spec->expressions[i].name);
        free(spec->expressions[i].expression);
    }
    free(spec->expressions);

    memset(spec, 0, sizeof(*spec));
}
