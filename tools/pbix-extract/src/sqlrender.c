#include "sqlrender.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "buffer.h"

/*
 * Rendering threads one status through every recursive call, since C has no exceptions. `refused`
 * separates "this expression has no SQL rendering" (which drops the visual with a reason) from an
 * allocation failure, which is a hard error the caller must not mistake for a refusal.
 */
typedef struct {
    int refused;
    int out_of_memory;
    char reason[256];
} RenderStatus;

/* The alias bindings in scope: the visual query's FROM, with a filter's own FROM overlaid. */
typedef struct {
    const QuerySource *sources;
    size_t count;
    const QuerySource *overlay;
    size_t overlay_count;
} AliasMap;

static void refuse(RenderStatus *status, const char *format, ...)
    __attribute__((format(printf, 2, 3)));

static void refuse(RenderStatus *status, const char *format, ...)
{
    va_list args;

    if (status->refused || status->out_of_memory) {
        return;
    }
    status->refused = 1;
    va_start(args, format);
    vsnprintf(status->reason, sizeof(status->reason), format, args);
    va_end(args);
}

/* True when the alias is bound by either the filter's own FROM or the visual query's. */
static int alias_is_bound(const AliasMap *aliases, const char *alias)
{
    size_t i;

    for (i = 0; i < aliases->overlay_count; i++) {
        if (strcmp(aliases->overlay[i].alias, alias) == 0) {
            return 1;
        }
    }
    for (i = 0; i < aliases->count; i++) {
        if (strcmp(aliases->sources[i].alias, alias) == 0) {
            return 1;
        }
    }
    return 0;
}

/*
 * Bracket-quotes an identifier, escaping any closing bracket, so a model name with a space or a
 * reserved word survives into parseable T-SQL.
 */
static int append_quoted(Buffer *out, const char *identifier)
{
    const char *cursor = identifier;

    if (buffer_append_str(out, "[") != 0) {
        return -1;
    }
    for (; *cursor != '\0'; cursor++) {
        if (*cursor == ']') {
            if (buffer_append_str(out, "]]") != 0) {
                return -1;
            }
        } else if (buffer_append(out, cursor, 1) != 0) {
            return -1;
        }
    }
    return buffer_append_str(out, "]");
}

static const char *aggregate_sql(AggregateFunction function, RenderStatus *status)
{
    switch (function) {
    case AGGREGATE_SUM:
        return "SUM";
    case AGGREGATE_AVERAGE:
        return "AVG";
    case AGGREGATE_COUNT:
        return "COUNT";
    case AGGREGATE_MIN:
        return "MIN";
    case AGGREGATE_MAX:
        return "MAX";
    case AGGREGATE_COUNT_NON_NULL:
        /* Power BI's "count non-null" is SQL's plain COUNT(expression), which ignores nulls. */
        return "COUNT";
    }

    refuse(status, "an aggregate function with no SQL rendering");
    return NULL;
}

static const char *comparison_sql(ComparisonOperator comparison, RenderStatus *status)
{
    switch (comparison) {
    case COMPARISON_EQUAL:
        return "=";
    case COMPARISON_NOT_EQUAL:
        return "<>";
    case COMPARISON_GREATER_THAN:
        return ">";
    case COMPARISON_GREATER_THAN_OR_EQUAL:
        return ">=";
    case COMPARISON_LESS_THAN:
        return "<";
    case COMPARISON_LESS_THAN_OR_EQUAL:
        return "<=";
    }

    refuse(status, "a comparison operator with no SQL rendering");
    return NULL;
}

/*
 * Renders the qualifier of a column/measure reference. Power BI names either an alias from the
 * query's FROM or an entity directly; an alias is kept as the alias (the FROM binds it), and a
 * direct entity is used as written. A reference to an alias the query never bound is refused rather
 * than guessed at, since guessing would attach the field to the wrong table and so to the wrong
 * lineage node.
 */
static int append_source(
    Buffer *out, const char *alias, const char *entity,
    const AliasMap *aliases, RenderStatus *status)
{
    if (alias != NULL && alias[0] != '\0') {
        if (!alias_is_bound(aliases, alias)) {
            refuse(status,
                "a reference to source alias '%s', which the query's FROM does not bind", alias);
            return -1;
        }
        return append_quoted(out, alias);
    }

    if (entity != NULL && entity[0] != '\0') {
        return append_quoted(out, entity);
    }

    refuse(status, "a reference with neither a source alias nor an entity");
    return -1;
}

static int append_expression(
    Buffer *out, const QueryExpr *expression, const AliasMap *aliases, RenderStatus *status);

/*
 * The single-expression case is the common one and renders as a plain IN list. The multi-expression
 * case renders as ORed tuples of ANDed equalities, which is the portable spelling of a row-value IN.
 */
static int append_in(
    Buffer *out, const QueryExpr *expression, const AliasMap *aliases, RenderStatus *status)
{
    size_t row;
    size_t column;

    if (expression->in_expression_count == 1) {
        if (append_expression(out, expression->in_expressions[0], aliases, status) != 0
            || buffer_append_str(out, " IN (") != 0) {
            return -1;
        }
        for (row = 0; row < expression->in_value_count; row++) {
            if (row > 0 && buffer_append_str(out, ", ") != 0) {
                return -1;
            }
            if (append_expression(out, expression->in_values[row][0], aliases, status) != 0) {
                return -1;
            }
        }
        return buffer_append_str(out, ")");
    }

    if (buffer_append_str(out, "(") != 0) {
        return -1;
    }
    for (row = 0; row < expression->in_value_count; row++) {
        if (row > 0 && buffer_append_str(out, " OR ") != 0) {
            return -1;
        }
        if (buffer_append_str(out, "(") != 0) {
            return -1;
        }
        for (column = 0; column < expression->in_expression_count; column++) {
            if (column > 0 && buffer_append_str(out, " AND ") != 0) {
                return -1;
            }
            if (append_expression(out, expression->in_expressions[column], aliases, status) != 0
                || buffer_append_str(out, " = ") != 0
                || append_expression(out, expression->in_values[row][column], aliases, status) != 0) {
                return -1;
            }
        }
        if (buffer_append_str(out, ")") != 0) {
            return -1;
        }
    }
    return buffer_append_str(out, ")");
}

/* True when `text` is a plain decimal or scientific number: an optional sign, digits with an optional
 * fraction, and an optional exponent. */
static int is_plain_number(const char *text, size_t length)
{
    size_t i = 0;
    int digits = 0;

    if (i < length && (text[i] == '-' || text[i] == '+')) {
        i++;
    }
    while (i < length && text[i] >= '0' && text[i] <= '9') {
        i++;
        digits++;
    }
    if (i < length && text[i] == '.') {
        i++;
        while (i < length && text[i] >= '0' && text[i] <= '9') {
            i++;
            digits++;
        }
    }
    if (digits == 0) {
        return 0;
    }
    if (i < length && (text[i] == 'E' || text[i] == 'e')) {
        i++;
        if (i < length && (text[i] == '-' || text[i] == '+')) {
            i++;
        }
        if (i >= length || text[i] < '0' || text[i] > '9') {
            return 0;
        }
        while (i < length && text[i] >= '0' && text[i] <= '9') {
            i++;
        }
    }
    return i == length;
}

/*
 * Writes a Power BI query literal as T-SQL. Power BI types its literals with a suffix or a prefix
 * (2020L, 1.5D, 3.25M, datetime'2020-01-01T00:00:00', true), none of which T-SQL parses, so each is
 * written in its T-SQL form: the bare number, the quoted date text, 1 or 0, NULL. A text literal is
 * already T-SQL ('it''s', with the quote doubled) and is written as it is, as is any form not listed.
 */
static int append_literal(Buffer *out, const char *literal)
{
    static const char *const typed_prefixes[] = { "datetimeoffset'", "datetime'", "date'", "time'" };
    size_t length;
    size_t i;

    if (literal == NULL) {
        return buffer_append_str(out, "NULL");
    }
    length = strlen(literal);

    if (strcmp(literal, "true") == 0) {
        return buffer_append_str(out, "1");
    }
    if (strcmp(literal, "false") == 0) {
        return buffer_append_str(out, "0");
    }
    if (strcmp(literal, "null") == 0) {
        return buffer_append_str(out, "NULL");
    }

    for (i = 0; i < sizeof(typed_prefixes) / sizeof(typed_prefixes[0]); i++) {
        size_t prefix_length = strlen(typed_prefixes[i]);

        if (length > prefix_length && strncmp(literal, typed_prefixes[i], prefix_length) == 0) {
            /* Keep the quote that ends the prefix: the rest is the quoted value. */
            return buffer_append_str(out, literal + prefix_length - 1);
        }
    }

    if (length > 1
        && (literal[length - 1] == 'L' || literal[length - 1] == 'D' || literal[length - 1] == 'M')
        && is_plain_number(literal, length - 1)) {
        return buffer_append(out, literal, length - 1);
    }

    return buffer_append_str(out, literal);
}

static int append_expression(
    Buffer *out, const QueryExpr *expression, const AliasMap *aliases, RenderStatus *status)
{
    const char *keyword;

    switch (expression->kind) {
    case EXPR_COLUMN:
        if (append_source(out, expression->source_alias, expression->source_entity, aliases, status) != 0
            || buffer_append_str(out, ".") != 0
            || append_quoted(out, expression->property) != 0) {
            return -1;
        }
        return 0;

    /* A measure is model-computed business logic with no warehouse column behind it. It is rendered
     * as a qualified name so the reference is visible and the entity it belongs to still resolves;
     * the DAX defining it lives in the model half of the file, not here. */
    case EXPR_MEASURE:
        if (append_source(out, expression->source_alias, expression->source_entity, aliases, status) != 0
            || buffer_append_str(out, ".") != 0
            || append_quoted(out, expression->property) != 0) {
            return -1;
        }
        return 0;

    /* A hierarchy level is rendered as its LEVEL, which is the field the visual groups by and the
     * name a reader would look for ("Month", not "Fiscal"). */
    case EXPR_HIERARCHY_LEVEL:
        if (append_source(out, expression->source_alias, expression->source_entity, aliases, status) != 0
            || buffer_append_str(out, ".") != 0
            || append_quoted(out, expression->level) != 0) {
            return -1;
        }
        return 0;

    case EXPR_AGGREGATION:
        keyword = aggregate_sql(expression->function, status);
        if (keyword == NULL
            || buffer_append_str(out, keyword) != 0
            || buffer_append_str(out, "(") != 0
            || append_expression(out, expression->inner, aliases, status) != 0
            || buffer_append_str(out, ")") != 0) {
            return -1;
        }
        return 0;

    case EXPR_LITERAL:
        return append_literal(out, expression->literal);

    case EXPR_IN:
        return append_in(out, expression, aliases, status);

    case EXPR_NOT:
        if (buffer_append_str(out, "NOT (") != 0
            || append_expression(out, expression->inner, aliases, status) != 0
            || buffer_append_str(out, ")") != 0) {
            return -1;
        }
        return 0;

    case EXPR_LOGICAL:
        if (buffer_append_str(out, "(") != 0
            || append_expression(out, expression->left, aliases, status) != 0
            || buffer_append_str(out, expression->is_or ? " OR " : " AND ") != 0
            || append_expression(out, expression->right, aliases, status) != 0
            || buffer_append_str(out, ")") != 0) {
            return -1;
        }
        return 0;

    case EXPR_COMPARISON:
        keyword = comparison_sql(expression->comparison, status);
        if (keyword == NULL
            || buffer_append_str(out, "(") != 0
            || append_expression(out, expression->left, aliases, status) != 0
            || buffer_append_str(out, " ") != 0
            || buffer_append_str(out, keyword) != 0
            || buffer_append_str(out, " ") != 0
            || append_expression(out, expression->right, aliases, status) != 0
            || buffer_append_str(out, ")") != 0) {
            return -1;
        }
        return 0;
    }

    refuse(status, "an expression type that has no SQL rendering");
    return -1;
}

int sql_render_visual(
    const ReportVisual *visual,
    const ReportFilter *page_filters, size_t page_filter_count,
    char **sql_out, char *refusal, size_t refusal_size)
{
    Buffer out;
    RenderStatus status;
    AliasMap aliases;
    size_t i;
    size_t predicate_count = 0;
    int result = -1;

    *sql_out = NULL;
    buffer_init(&out);
    memset(&status, 0, sizeof(status));

    aliases.sources = visual->query.from;
    aliases.count = visual->query.from_count;
    aliases.overlay = NULL;
    aliases.overlay_count = 0;

    if (buffer_append_str(&out, "SELECT ") != 0) {
        goto done;
    }

    if (visual->query.select_count == 0) {
        /* Cannot happen for a visual the reader kept (it requires at least one resolved field), and
         * is rendered as a valid statement rather than an empty list so the output always parses. */
        if (buffer_append_str(&out, "*") != 0) {
            goto done;
        }
    } else {
        for (i = 0; i < visual->query.select_count; i++) {
            if (i > 0 && buffer_append_str(&out, ", ") != 0) {
                goto done;
            }
            if (append_expression(&out, visual->query.select[i].expression, &aliases, &status) != 0
                || buffer_append_str(&out, " AS ") != 0
                || append_quoted(&out, visual->query.select[i].name) != 0) {
                goto done;
            }
        }
    }

    if (visual->query.from_count > 0) {
        if (buffer_append_str(&out, " FROM ") != 0) {
            goto done;
        }
        for (i = 0; i < visual->query.from_count; i++) {
            /* A visual query relates its entities through the model's relationships rather than by
             * writing joins, so there is no ON predicate to render. The entities are listed as the
             * tables this visual reads, which is exactly what consumption lineage needs from it. */
            if (i > 0 && buffer_append_str(&out, ", ") != 0) {
                goto done;
            }
            if (append_quoted(&out, visual->query.from[i].entity) != 0
                || buffer_append_str(&out, " AS ") != 0
                || append_quoted(&out, visual->query.from[i].alias) != 0) {
                goto done;
            }
        }
    }

    /* Page filters first, then the visual's own: both apply, and both narrow the question. */
    for (i = 0; i < page_filter_count + visual->filter_count; i++) {
        const ReportFilter *filter = i < page_filter_count
            ? &page_filters[i]
            : &visual->filters[i - page_filter_count];

        /* A filter carries its own FROM bindings, so its aliases resolve even when they are not the
         * visual query's. Where an alias is shared, the visual's binding is the same entity. */
        aliases.overlay = filter->from;
        aliases.overlay_count = filter->from_count;

        if (buffer_append_str(&out, predicate_count == 0 ? " WHERE " : " AND ") != 0
            || append_expression(&out, filter->condition, &aliases, &status) != 0) {
            goto done;
        }
        predicate_count++;
    }

    aliases.overlay = NULL;
    aliases.overlay_count = 0;

    if (visual->query.order_by_count > 0) {
        if (buffer_append_str(&out, " ORDER BY ") != 0) {
            goto done;
        }
        for (i = 0; i < visual->query.order_by_count; i++) {
            if (i > 0 && buffer_append_str(&out, ", ") != 0) {
                goto done;
            }
            if (append_expression(&out, visual->query.order_by[i].expression, &aliases, &status) != 0
                || buffer_append_str(&out, visual->query.order_by[i].descending ? " DESC" : " ASC") != 0) {
                goto done;
            }
        }
    }

    if (buffer_append_str(&out, ";") != 0 || buffer_append(&out, "", 1) != 0) {
        goto done;
    }

    *sql_out = (char *)out.data;
    buffer_init(&out);
    result = 0;

done:
    buffer_free(&out);
    if (result != 0 && status.refused) {
        if (refusal != NULL && refusal_size > 0) {
            snprintf(refusal, refusal_size, "%s", status.reason);
        }
        return 1;
    }
    return result;
}
