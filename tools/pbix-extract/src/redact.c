#include "redact.h"

#include <stdlib.h>
#include <string.h>

#define REDACTION "<redacted>"

/*
 * The M functions whose first string argument names a location rather than describing data.
 *
 * Every entry is a function that reaches outside the file: a filesystem path, a server, a URL, a
 * SharePoint or blob endpoint. Functions that shape or decode data already in hand (Excel.Workbook,
 * Csv.Document, Json.Document, Binary.FromText, Table.FromRows) are deliberately absent: their
 * first argument is either a nested call or inline data, so redacting them would either duplicate
 * work done by the inner call or destroy schema information.
 */
static const char *const LOCATION_FUNCTIONS[] = {
    "File.Contents",
    "Folder.Files",
    "Folder.Contents",
    "Sql.Database",
    "Sql.Databases",
    "Odbc.DataSource",
    "Odbc.Query",
    "OleDb.DataSource",
    "OleDb.Query",
    "Web.Contents",
    "Web.Page",
    "AzureStorage.Blobs",
    "AzureStorage.Tables",
    "AzureStorage.DataLake",
    "SharePoint.Files",
    "SharePoint.Contents",
    "SharePoint.Tables",
    "Access.Database",
    "Oracle.Database",
    "MySQL.Database",
    "PostgreSQL.Database",
    "Teradata.Database",
    "Snowflake.Databases",
    "GoogleBigQuery.Database",
    "Hdfs.Files",
    "Hdfs.Contents",
};

static const size_t LOCATION_FUNCTION_COUNT =
    sizeof(LOCATION_FUNCTIONS) / sizeof(LOCATION_FUNCTIONS[0]);

/*
 * True when `c` can be part of an M identifier. Used to confirm a matched function name stands on
 * its own: without this, "My.File.Contents" would match "File.Contents" and be rewritten, altering
 * an expression the redaction has no business touching.
 */
static int is_identifier_char(char c)
{
    return (c >= 'A' && c <= 'Z')
        || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c == '_'
        || c == '.';
}

/*
 * Finds the end of the string literal that starts at `open` (which must point at its opening
 * quote), returning a pointer to its closing quote, or NULL when the literal is unterminated.
 *
 * M escapes a quote inside a literal by doubling it (""), so a doubled quote continues the literal
 * rather than ending it. Scanning without that rule would stop early on a path containing a quote
 * and splice the redaction into the middle of an expression.
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
 * Matches a location-naming function call at `p` whose first argument is a string literal.
 *
 * On a match, yields the literal's opening and closing quotes and returns 1. Returns 0 otherwise,
 * including when the first argument is a nested call: that call names the location itself and gets
 * redacted when the scan reaches it.
 */
static int match_location_call(
    const char *text, const char *p, const char **literal_open, const char **literal_close)
{
    size_t i;

    for (i = 0; i < LOCATION_FUNCTION_COUNT; i++) {
        const char *name = LOCATION_FUNCTIONS[i];
        size_t length = strlen(name);
        const char *cursor;
        const char *close;

        if (strncmp(p, name, length) != 0) {
            continue;
        }

        /* The name must not be the tail of a longer identifier. */
        if (p > text && is_identifier_char(*(p - 1))) {
            continue;
        }

        cursor = p + length;
        while (*cursor == ' ' || *cursor == '\t' || *cursor == '\r' || *cursor == '\n') {
            cursor++;
        }
        if (*cursor != '(') {
            continue;
        }
        cursor++;
        while (*cursor == ' ' || *cursor == '\t' || *cursor == '\r' || *cursor == '\n') {
            cursor++;
        }
        if (*cursor != '"') {
            /* A nested call or a variable reference, not a literal path. */
            return 0;
        }

        close = literal_end(cursor);
        if (close == NULL) {
            return 0;
        }

        *literal_open = cursor;
        *literal_close = close;
        return 1;
    }

    return 0;
}

char *redact_m_expression(const char *expression)
{
    size_t capacity;
    size_t used = 0;
    char *result;
    const char *p;

    if (expression == NULL) {
        return NULL;
    }

    /*
     * The redaction is shorter than any path it replaces in practice, but a literal shorter than
     * "<redacted>" would grow the text, so the buffer allows for the worst case: every byte of the
     * input replaced by the full redaction.
     */
    capacity = strlen(expression) * (sizeof(REDACTION) - 1) + sizeof(REDACTION);
    result = (char *)malloc(capacity);
    if (result == NULL) {
        return NULL;
    }

    for (p = expression; *p != '\0'; ) {
        const char *literal_open;
        const char *literal_close;

        if (match_location_call(expression, p, &literal_open, &literal_close)) {
            size_t prefix = (size_t)(literal_open - p) + 1;  /* through the opening quote */

            memcpy(result + used, p, prefix);
            used += prefix;
            memcpy(result + used, REDACTION, sizeof(REDACTION) - 1);
            used += sizeof(REDACTION) - 1;
            result[used++] = '"';
            p = literal_close + 1;
            continue;
        }

        result[used++] = *p++;
    }

    result[used] = '\0';
    return result;
}
