#include "msource.h"

#include <stdlib.h>
#include <string.h>

/*
 * The source functions this matcher recognizes well enough to NAME in a warning. Only
 * "Sql.Database" is resolvable; the rest are listed so an unresolved table says why it could not be
 * resolved rather than only that it was not. Ordered longest-first where one name prefixes another,
 * so "Sql.Databases" is tested before "Sql.Database".
 */
static const char *const KNOWN_SHAPES[] = {
    "Sql.Databases",
    "Sql.Database",
    "Excel.Workbook",
    "Csv.Document",
    "Json.Document",
    "Web.Contents",
    "SharePoint.Files",
    "SharePoint.Tables",
    "Odbc.DataSource",
    "OleDb.DataSource",
    "AnalysisServices.Database",
    "Oracle.Database",
    "PostgreSQL.Database",
    "MySQL.Database",
    "Snowflake.Databases",
    "Table.Combine",
    "Table.NestedJoin",
};

static const size_t KNOWN_SHAPE_COUNT = sizeof(KNOWN_SHAPES) / sizeof(KNOWN_SHAPES[0]);

/* An M identifier character. A function name is dotted, so '.' counts. */
static int is_ident_char(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        || c == '_' || c == '.';
}

/*
 * Finds the end of the string literal opening at `open`, returning its closing quote or NULL when
 * unterminated. M escapes a quote by doubling it, so a doubled quote continues the literal; scanning
 * without that rule would end a literal early and read the rest of it as syntax.
 */
static const char *literal_end(const char *open)
{
    const char *p = open + 1;

    while (*p != '\0') {
        if (*p == '"') {
            if (*(p + 1) == '"') {
                p += 2;
                continue;
            }
            return p;
        }
        p++;
    }
    return NULL;
}

/*
 * Copies the literal between `open` and `close` (exclusive of both quotes), collapsing M's doubled
 * quotes back to single ones. Returns a fresh allocation, or NULL on allocation failure.
 */
static char *literal_text(const char *open, const char *close)
{
    size_t span = (size_t)(close - open) - 1;
    char *out = malloc(span + 1);
    const char *p;
    size_t n = 0;

    if (out == NULL) {
        return NULL;
    }

    for (p = open + 1; p < close; p++) {
        if (*p == '"' && *(p + 1) == '"' && (p + 1) < close) {
            out[n++] = '"';
            p++;
            continue;
        }
        out[n++] = *p;
    }
    out[n] = '\0';
    return out;
}

/* Advances past spaces, tabs and newlines. M formatting varies, so no position is assumed. */
static const char *skip_space(const char *p)
{
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') {
        p++;
    }
    return p;
}

/*
 * Finds the first occurrence of identifier `name` used as a call head, i.e. not as the tail of a
 * longer identifier and followed (after whitespace) by '('. Returns the position of the name, or
 * NULL.
 */
static const char *find_call(const char *text, const char *name)
{
    size_t length = strlen(name);
    const char *p = text;

    while ((p = strstr(p, name)) != NULL) {
        const char *after = p + length;

        if (p > text && is_ident_char(*(p - 1))) {
            p = after;
            continue;
        }
        if (is_ident_char(*after)) {
            p = after;
            continue;
        }
        if (*skip_space(after) != '(') {
            p = after;
            continue;
        }
        return p;
    }
    return NULL;
}

/*
 * Reads the Nth (0-based) string-literal argument of the call whose name begins at `call`, stopping
 * at the call's closing parenthesis. Returns a fresh string, or NULL when that argument is absent or
 * is not a plain literal.
 *
 * Only the top level of the argument list is considered: a nested call's own arguments are skipped
 * by depth tracking, so Sql.Database(SomeFn("x"), "db") does not mistake "x" for the first argument.
 * A non-literal argument occupies its position rather than being skipped over, which is what makes
 * that distinction hold.
 */
static char *literal_argument(const char *call, size_t wanted)
{
    const char *p = strchr(call, '(');
    int depth = 0;
    size_t index = 0;

    if (p == NULL) {
        return NULL;
    }

    for (; *p != '\0'; p++) {
        if (*p == '"') {
            const char *close = literal_end(p);

            if (close == NULL) {
                return NULL;
            }
            /* A literal sitting directly in this call's argument list, at the position we want. */
            if (depth == 1 && index == wanted) {
                return literal_text(p, close);
            }
            p = close;
            continue;
        }

        if (*p == '(' || *p == '[' || *p == '{') {
            depth++;
            continue;
        }
        if (*p == ')' || *p == ']' || *p == '}') {
            depth--;
            if (depth == 0) {
                return NULL; /* the call closed before reaching the wanted argument */
            }
            continue;
        }
        if (*p == ',' && depth == 1) {
            index++;
            if (index > wanted) {
                return NULL;
            }
        }
    }
    return NULL;
}

/*
 * Reads the string value of a record field spelled `Field="value"` (whitespace tolerated around the
 * '='), searching the whole expression. Returns a fresh string or NULL.
 *
 * Scanning the whole text rather than a located record is deliberate: the navigation step that
 * carries Schema/Item can appear anywhere in a let chain, often several steps after the source, and
 * anchoring to a fixed position would break on the transform steps PowerBI routinely inserts.
 */
static char *record_field(const char *text, const char *field)
{
    size_t length = strlen(field);
    const char *p = text;

    while ((p = strstr(p, field)) != NULL) {
        const char *after = p + length;
        const char *value;

        if (p > text && is_ident_char(*(p - 1))) {
            p = after;
            continue;
        }

        value = skip_space(after);
        if (*value != '=') {
            p = after;
            continue;
        }
        value = skip_space(value + 1);
        if (*value != '"') {
            p = after;
            continue;
        }

        {
            const char *close = literal_end(value);

            if (close == NULL) {
                return NULL;
            }
            return literal_text(value, close);
        }
    }
    return NULL;
}

/* Records which recognized shape appeared, for the unresolved warning. */
static char *first_known_shape(const char *text)
{
    size_t i;

    for (i = 0; i < KNOWN_SHAPE_COUNT; i++) {
        if (find_call(text, KNOWN_SHAPES[i]) != NULL) {
            char *copy = malloc(strlen(KNOWN_SHAPES[i]) + 1);

            if (copy == NULL) {
                return NULL;
            }
            strcpy(copy, KNOWN_SHAPES[i]);
            return copy;
        }
    }
    return NULL;
}

void msource_free(MSourceResolution *resolution)
{
    if (resolution == NULL) {
        return;
    }
    free(resolution->server);
    free(resolution->database);
    free(resolution->schema);
    free(resolution->item);
    free(resolution->unresolved_shape);
    memset(resolution, 0, sizeof(*resolution));
}

int msource_resolve(const char *expression, MSourceResolution *out)
{
    const char *call;

    if (out == NULL) {
        return -1;
    }
    memset(out, 0, sizeof(*out));
    if (expression == NULL) {
        return 0;
    }

    call = find_call(expression, "Sql.Database");

    /*
     * Sql.Databases("server") indexes a database by name downstream rather than naming it in the
     * call, and find_call("Sql.Database") would match its prefix. Treat it as a recognized but
     * unresolved shape rather than reading its first argument as a database name.
     */
    if (call != NULL && strncmp(call, "Sql.Databases", strlen("Sql.Databases")) == 0) {
        call = NULL;
    }

    if (call != NULL) {
        char *server = literal_argument(call, 0);
        char *database = literal_argument(call, 1);
        char *schema = record_field(expression, "Schema");
        char *item = record_field(expression, "Item");

        /*
         * All four parts are required. A native-query source (Sql.Database(s, db, [Query="..."]))
         * names its objects inside SQL text rather than a Schema/Item pair, so it correctly lands
         * here with no schema/item and stays unresolved: guessing a target from a partial match is
         * exactly the failure this design refuses.
         */
        if (server != NULL && database != NULL && schema != NULL && item != NULL) {
            out->resolved = 1;
            out->server = server;
            out->database = database;
            out->schema = schema;
            out->item = item;
            return 0;
        }

        free(server);
        free(database);
        free(schema);
        free(item);
    }

    out->unresolved_shape = first_known_shape(expression);
    return 0;
}
