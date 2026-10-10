#include "reportlayout.h"

#include <jansson.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "buffer.h"
#include "datamodel.h"
#include "sqlrender.h"

/*
 * Threaded through expression parsing, since C has no exceptions.
 *
 * A NULL return with `refused == 0` means the node was ABSENT, which is a different thing from a
 * node that could not be represented: a filter entry with no condition constrains nothing and is
 * simply not recorded, while a filter whose condition uses an unsupported expression must drop the
 * whole visual. Confusing the two would silently widen a question.
 */
typedef struct {
    int refused;
    int out_of_memory;
    char reason[256];
} ExprStatus;

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

/* Returns a string member's value, or NULL when absent or not a string. */
static const char *json_text(json_t *object, const char *key)
{
    json_t *value = json_object_get(object, key);

    return json_is_string(value) ? json_string_value(value) : NULL;
}

/* Duplicates a string member, or NULL when absent/empty. */
static char *dup_text(json_t *object, const char *key)
{
    const char *text = json_text(object, key);

    if (text == NULL || text[0] == '\0') {
        return NULL;
    }
    return strdup(text);
}

/* Strips Power BI's surrounding single quotes from a stored text literal. */
static char *unquote(const char *value)
{
    size_t length = strlen(value);

    if (length >= 2 && value[0] == '\'' && value[length - 1] == '\'') {
        char *copy = (char *)malloc(length - 1);

        if (copy == NULL) {
            return NULL;
        }
        memcpy(copy, value + 1, length - 2);
        copy[length - 2] = '\0';
        return copy;
    }
    return strdup(value);
}

/* Grows an array of records by one, returning the zeroed new slot or NULL. */
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

/*
 * Reads a visual's authored title, which Power BI stores as a single-quoted literal nested under
 * the visual's container objects: <objects_key>.title[].properties.text.expr.Literal.Value.
 *
 * `objects_key` differs by report format ("vcObjects" in the single-part layout,
 * "visualContainerObjects" in the split one); everything below that key is identical, so one reader
 * serves both.
 *
 * The title matters more than its depth suggests: it is the report author's own words for the
 * question the visual answers, which is exactly the phrasing a person would use when asking it
 * again.
 */
static char *read_title(json_t *single_visual, const char *objects_key)
{
    json_t *vc_objects = json_object_get(single_visual, objects_key);
    json_t *titles;
    size_t index;
    json_t *entry;

    if (!json_is_object(vc_objects)) {
        return NULL;
    }
    titles = json_object_get(vc_objects, "title");
    if (!json_is_array(titles)) {
        return NULL;
    }

    json_array_foreach(titles, index, entry) {
        json_t *properties = json_object_get(entry, "properties");
        json_t *text = properties != NULL ? json_object_get(properties, "text") : NULL;
        json_t *expr = text != NULL ? json_object_get(text, "expr") : NULL;
        json_t *literal = expr != NULL ? json_object_get(expr, "Literal") : NULL;
        const char *value = literal != NULL ? json_text(literal, "Value") : NULL;

        if (value != NULL && value[0] != '\0') {
            return unquote(value);
        }
    }

    return NULL;
}

void query_expr_free(QueryExpr *expression)
{
    size_t row;
    size_t column;

    if (expression == NULL) {
        return;
    }

    free(expression->source_alias);
    free(expression->source_entity);
    free(expression->property);
    free(expression->hierarchy);
    free(expression->level);
    free(expression->literal);

    query_expr_free(expression->inner);
    query_expr_free(expression->left);
    query_expr_free(expression->right);

    for (column = 0; column < expression->in_expression_count; column++) {
        query_expr_free(expression->in_expressions[column]);
    }
    free(expression->in_expressions);

    /* The value tuples are a rectangular array of rows, each as wide as `in_expression_count`.
     * Freeing it is the easiest leak site in this tree, so it is done in exactly one place and
     * reached from both the parse error path and here. */
    for (row = 0; row < expression->in_value_count; row++) {
        if (expression->in_values[row] == NULL) {
            continue;
        }
        for (column = 0; column < expression->in_expression_count; column++) {
            query_expr_free(expression->in_values[row][column]);
        }
        free(expression->in_values[row]);
    }
    free(expression->in_values);

    free(expression);
}

static void query_source_free(QuerySource *sources, size_t count)
{
    size_t i;

    for (i = 0; i < count; i++) {
        free(sources[i].alias);
        free(sources[i].entity);
    }
    free(sources);
}

void report_filters_free(ReportFilter *filters, size_t count)
{
    size_t i;

    for (i = 0; i < count; i++) {
        free(filters[i].name);
        query_source_free(filters[i].from, filters[i].from_count);
        query_expr_free(filters[i].condition);
    }
    free(filters);
}

static void visual_query_free(VisualQuery *query)
{
    size_t i;

    query_source_free(query->from, query->from_count);
    for (i = 0; i < query->select_count; i++) {
        free(query->select[i].name);
        query_expr_free(query->select[i].expression);
    }
    free(query->select);
    for (i = 0; i < query->order_by_count; i++) {
        query_expr_free(query->order_by[i].expression);
    }
    free(query->order_by);
    memset(query, 0, sizeof(*query));
}

static void set_refusal(ExprStatus *status, const char *format, ...)
    __attribute__((format(printf, 2, 3)));

static void set_refusal(ExprStatus *status, const char *format, ...)
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

/* Allocates a zeroed node of the given kind, or records out-of-memory and returns NULL. */
static QueryExpr *expr_new(ExprKind kind, ExprStatus *status)
{
    QueryExpr *expression = (QueryExpr *)calloc(1, sizeof(*expression));

    if (expression == NULL) {
        status->out_of_memory = 1;
        return NULL;
    }
    expression->kind = kind;
    return expression;
}

/* Duplicates a string, recording out-of-memory. Returns NULL when the source is NULL. */
static char *dup_or_null(const char *text, ExprStatus *status)
{
    char *copy;

    if (text == NULL) {
        return NULL;
    }
    copy = strdup(text);
    if (copy == NULL) {
        status->out_of_memory = 1;
    }
    return copy;
}

/* Reads a SourceRef, which names either an alias from the query's FROM or an entity. */
static void read_source_ref(json_t *owner, char **alias, char **entity, ExprStatus *status)
{
    json_t *expression = json_object_get(owner, "Expression");
    json_t *source_ref = expression != NULL ? json_object_get(expression, "SourceRef") : NULL;

    *alias = NULL;
    *entity = NULL;
    if (!json_is_object(source_ref)) {
        return;
    }
    *alias = dup_or_null(json_text(source_ref, "Source"), status);
    *entity = dup_or_null(json_text(source_ref, "Entity"), status);
}

/* Power BI's numeric aggregate codes, as written into a visual's prototypeQuery. */
static int map_aggregate(json_t *aggregation, AggregateFunction *function, ExprStatus *status)
{
    json_t *code = json_object_get(aggregation, "Function");

    if (!json_is_integer(code)) {
        set_refusal(status, "an aggregation with no function code");
        return -1;
    }

    switch (json_integer_value(code)) {
    case 0: *function = AGGREGATE_SUM; return 0;
    case 1: *function = AGGREGATE_AVERAGE; return 0;
    case 2: *function = AGGREGATE_COUNT; return 0;
    case 3: *function = AGGREGATE_MIN; return 0;
    case 4: *function = AGGREGATE_MAX; return 0;
    case 5: *function = AGGREGATE_COUNT_NON_NULL; return 0;
    default:
        set_refusal(status, "aggregate function code %lld",
            (long long)json_integer_value(code));
        return -1;
    }
}

static int map_comparison(json_t *comparison, ComparisonOperator *result, ExprStatus *status)
{
    json_t *kind = json_object_get(comparison, "ComparisonKind");

    if (!json_is_integer(kind)) {
        set_refusal(status, "a comparison with no kind");
        return -1;
    }

    switch (json_integer_value(kind)) {
    case 0: *result = COMPARISON_EQUAL; return 0;
    case 1: *result = COMPARISON_GREATER_THAN; return 0;
    case 2: *result = COMPARISON_GREATER_THAN_OR_EQUAL; return 0;
    case 3: *result = COMPARISON_LESS_THAN; return 0;
    case 4: *result = COMPARISON_LESS_THAN_OR_EQUAL; return 0;
    case 5: *result = COMPARISON_NOT_EQUAL; return 0;
    default:
        set_refusal(status, "comparison kind %lld", (long long)json_integer_value(kind));
        return -1;
    }
}

static QueryExpr *parse_expression(json_t *node, ExprStatus *status);

/* Parses an And/Or node, whose two operands are themselves expressions. */
static QueryExpr *parse_logical(json_t *node, int is_or, ExprStatus *status)
{
    QueryExpr *expression;
    json_t *left = json_object_get(node, "Left");
    json_t *right = json_object_get(node, "Right");

    if (!json_is_object(left) || !json_is_object(right)) {
        set_refusal(status, "a logical %s missing an operand", is_or ? "OR" : "AND");
        return NULL;
    }

    expression = expr_new(EXPR_LOGICAL, status);
    if (expression == NULL) {
        return NULL;
    }
    expression->is_or = is_or;
    expression->left = parse_expression(left, status);
    expression->right = parse_expression(right, status);
    if (expression->left == NULL || expression->right == NULL) {
        query_expr_free(expression);
        return NULL;
    }
    return expression;
}

/* Parses an In node: one or more expressions tested against a set of value tuples. */
static QueryExpr *parse_in(json_t *node, ExprStatus *status)
{
    QueryExpr *expression = expr_new(EXPR_IN, status);
    json_t *expressions = json_object_get(node, "Expressions");
    json_t *values = json_object_get(node, "Values");
    size_t index;
    json_t *entry;

    if (expression == NULL) {
        return NULL;
    }

    if (json_is_array(expressions)) {
        json_array_foreach(expressions, index, entry) {
            QueryExpr **grown = (QueryExpr **)realloc(expression->in_expressions,
                (expression->in_expression_count + 1) * sizeof(*grown));
            QueryExpr *parsed;

            if (grown == NULL) {
                status->out_of_memory = 1;
                query_expr_free(expression);
                return NULL;
            }
            expression->in_expressions = grown;
            parsed = parse_expression(entry, status);
            if (parsed == NULL) {
                query_expr_free(expression);
                return NULL;
            }
            expression->in_expressions[expression->in_expression_count++] = parsed;
        }
    }

    if (expression->in_expression_count == 0 || !json_is_array(values)
        || json_array_size(values) == 0) {
        set_refusal(status, "a membership test with no expressions or no values");
        query_expr_free(expression);
        return NULL;
    }

    json_array_foreach(values, index, entry) {
        QueryExpr ***grown;
        QueryExpr **tuple;
        size_t column;
        json_t *value;

        if (!json_is_array(entry) || json_array_size(entry) != expression->in_expression_count) {
            set_refusal(status,
                "a membership test whose value tuples do not match its expression count");
            query_expr_free(expression);
            return NULL;
        }

        grown = (QueryExpr ***)realloc(expression->in_values,
            (expression->in_value_count + 1) * sizeof(*grown));
        if (grown == NULL) {
            status->out_of_memory = 1;
            query_expr_free(expression);
            return NULL;
        }
        expression->in_values = grown;

        /* The row is published to the node zeroed BEFORE its members are parsed, so a failure part
         * way through the tuple still frees what was built via query_expr_free. */
        tuple = (QueryExpr **)calloc(expression->in_expression_count, sizeof(*tuple));
        if (tuple == NULL) {
            status->out_of_memory = 1;
            query_expr_free(expression);
            return NULL;
        }
        expression->in_values[expression->in_value_count++] = tuple;

        json_array_foreach(entry, column, value) {
            tuple[column] = parse_expression(value, status);
            if (tuple[column] == NULL) {
                query_expr_free(expression);
                return NULL;
            }
        }
    }

    return expression;
}

/*
 * Reads one expression node. The node's KIND is which property it carries, so the set is matched
 * explicitly and anything else is refused: silently ignoring an unknown node would corrupt the
 * meaning of the query or filter it appears in.
 *
 * Returns NULL with `status->refused` set when the node cannot be represented, and NULL with
 * `status->out_of_memory` set on allocation failure. Every failure frees the partial node first.
 */
static QueryExpr *parse_expression(json_t *node, ExprStatus *status)
{
    QueryExpr *expression;
    json_t *inner;

    if (status->refused || status->out_of_memory) {
        return NULL;
    }
    if (!json_is_object(node)) {
        set_refusal(status, "an expression that is not an object");
        return NULL;
    }

    inner = json_object_get(node, "Column");
    if (json_is_object(inner)) {
        const char *property = json_text(inner, "Property");

        if (property == NULL) {
            set_refusal(status, "a column reference with no property");
            return NULL;
        }
        expression = expr_new(EXPR_COLUMN, status);
        if (expression == NULL) {
            return NULL;
        }
        read_source_ref(inner, &expression->source_alias, &expression->source_entity, status);
        expression->property = dup_or_null(property, status);
        if (status->out_of_memory) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "Measure");
    if (json_is_object(inner)) {
        const char *property = json_text(inner, "Property");

        if (property == NULL) {
            set_refusal(status, "a measure reference with no property");
            return NULL;
        }
        expression = expr_new(EXPR_MEASURE, status);
        if (expression == NULL) {
            return NULL;
        }
        read_source_ref(inner, &expression->source_alias, &expression->source_entity, status);
        expression->property = dup_or_null(property, status);
        if (status->out_of_memory) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "HierarchyLevel");
    if (json_is_object(inner)) {
        const char *level = json_text(inner, "Level");
        json_t *hierarchy_expression = json_object_get(inner, "Expression");
        json_t *hierarchy = hierarchy_expression != NULL
            ? json_object_get(hierarchy_expression, "Hierarchy") : NULL;
        const char *hierarchy_name;

        if (level == NULL) {
            set_refusal(status, "a hierarchy level with no level name");
            return NULL;
        }
        if (!json_is_object(hierarchy)) {
            set_refusal(status, "a hierarchy level with no hierarchy");
            return NULL;
        }
        hierarchy_name = json_text(hierarchy, "Hierarchy");
        if (hierarchy_name == NULL) {
            set_refusal(status, "a hierarchy with no name");
            return NULL;
        }

        expression = expr_new(EXPR_HIERARCHY_LEVEL, status);
        if (expression == NULL) {
            return NULL;
        }
        read_source_ref(hierarchy, &expression->source_alias, &expression->source_entity, status);
        expression->hierarchy = dup_or_null(hierarchy_name, status);
        expression->level = dup_or_null(level, status);
        if (status->out_of_memory) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "Aggregation");
    if (json_is_object(inner)) {
        json_t *aggregated = json_object_get(inner, "Expression");

        if (!json_is_object(aggregated)) {
            set_refusal(status, "an aggregation with no expression");
            return NULL;
        }
        expression = expr_new(EXPR_AGGREGATION, status);
        if (expression == NULL) {
            return NULL;
        }
        if (map_aggregate(inner, &expression->function, status) != 0) {
            query_expr_free(expression);
            return NULL;
        }
        expression->inner = parse_expression(aggregated, status);
        if (expression->inner == NULL) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "Literal");
    if (json_is_object(inner)) {
        const char *value = json_text(inner, "Value");

        if (value == NULL) {
            set_refusal(status, "a literal with no value");
            return NULL;
        }
        expression = expr_new(EXPR_LITERAL, status);
        if (expression == NULL) {
            return NULL;
        }
        expression->literal = dup_or_null(value, status);
        if (status->out_of_memory) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "In");
    if (json_is_object(inner)) {
        return parse_in(inner, status);
    }

    inner = json_object_get(node, "Not");
    if (json_is_object(inner)) {
        json_t *negated = json_object_get(inner, "Expression");

        if (!json_is_object(negated)) {
            set_refusal(status, "a negation with no expression");
            return NULL;
        }
        expression = expr_new(EXPR_NOT, status);
        if (expression == NULL) {
            return NULL;
        }
        expression->inner = parse_expression(negated, status);
        if (expression->inner == NULL) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    inner = json_object_get(node, "And");
    if (json_is_object(inner)) {
        return parse_logical(inner, 0, status);
    }

    inner = json_object_get(node, "Or");
    if (json_is_object(inner)) {
        return parse_logical(inner, 1, status);
    }

    inner = json_object_get(node, "Comparison");
    if (json_is_object(inner)) {
        json_t *left = json_object_get(inner, "Left");
        json_t *right = json_object_get(inner, "Right");

        if (!json_is_object(left) || !json_is_object(right)) {
            set_refusal(status, "a comparison missing an operand");
            return NULL;
        }
        expression = expr_new(EXPR_COMPARISON, status);
        if (expression == NULL) {
            return NULL;
        }
        if (map_comparison(inner, &expression->comparison, status) != 0) {
            query_expr_free(expression);
            return NULL;
        }
        expression->left = parse_expression(left, status);
        expression->right = parse_expression(right, status);
        if (expression->left == NULL || expression->right == NULL) {
            query_expr_free(expression);
            return NULL;
        }
        return expression;
    }

    set_refusal(status, "an unrecognized node");
    return NULL;
}

/* Reads a From array into alias/entity bindings. Entries missing either half are skipped. */
static int parse_sources(
    json_t *from_array, QuerySource **sources, size_t *count, ExprStatus *status)
{
    size_t index;
    json_t *entry;

    if (!json_is_array(from_array)) {
        return 0;
    }

    json_array_foreach(from_array, index, entry) {
        const char *alias = json_text(entry, "Name");
        const char *entity = json_text(entry, "Entity");
        QuerySource *grown;

        if (alias == NULL || alias[0] == '\0' || entity == NULL || entity[0] == '\0') {
            continue;
        }

        grown = (QuerySource *)realloc(*sources, (*count + 1) * sizeof(*grown));
        if (grown == NULL) {
            status->out_of_memory = 1;
            return -1;
        }
        *sources = grown;
        (*sources)[*count].alias = strdup(alias);
        (*sources)[*count].entity = strdup(entity);
        if ((*sources)[*count].alias == NULL || (*sources)[*count].entity == NULL) {
            /* Publish the row before bailing so the half-filled entry is freed with the rest. */
            (*count)++;
            status->out_of_memory = 1;
            return -1;
        }
        (*count)++;
    }

    return 0;
}

/* Reads a visual's prototypeQuery: what it reads, what it projects, and in what order. */
static int parse_query(json_t *query_node, VisualQuery *query, ExprStatus *status)
{
    json_t *array;
    size_t index;
    json_t *entry;

    if (parse_sources(json_object_get(query_node, "From"), &query->from, &query->from_count,
            status) != 0) {
        return -1;
    }

    array = json_object_get(query_node, "Select");
    if (json_is_array(array)) {
        json_array_foreach(array, index, entry) {
            const char *name = json_text(entry, "Name");
            QuerySelection *grown;
            QueryExpr *parsed;

            if (name == NULL || name[0] == '\0') {
                continue;
            }

            parsed = parse_expression(entry, status);
            if (parsed == NULL) {
                return -1;
            }

            grown = (QuerySelection *)realloc(query->select,
                (query->select_count + 1) * sizeof(*grown));
            if (grown == NULL) {
                query_expr_free(parsed);
                status->out_of_memory = 1;
                return -1;
            }
            query->select = grown;
            query->select[query->select_count].name = strdup(name);
            query->select[query->select_count].expression = parsed;
            query->select_count++;
            if (query->select[query->select_count - 1].name == NULL) {
                status->out_of_memory = 1;
                return -1;
            }
        }
    }

    array = json_object_get(query_node, "OrderBy");
    if (json_is_array(array)) {
        json_array_foreach(array, index, entry) {
            json_t *expression_node = json_object_get(entry, "Expression");
            json_t *direction = json_object_get(entry, "Direction");
            QueryOrdering *grown;
            QueryExpr *parsed;

            if (!json_is_object(expression_node)) {
                continue;
            }

            parsed = parse_expression(expression_node, status);
            if (parsed == NULL) {
                return -1;
            }

            grown = (QueryOrdering *)realloc(query->order_by,
                (query->order_by_count + 1) * sizeof(*grown));
            if (grown == NULL) {
                query_expr_free(parsed);
                status->out_of_memory = 1;
                return -1;
            }
            query->order_by = grown;
            query->order_by[query->order_by_count].expression = parsed;
            /* Power BI encodes direction as 1 (ascending) or 2 (descending). */
            query->order_by[query->order_by_count].descending =
                json_is_integer(direction) && json_integer_value(direction) == 2;
            query->order_by_count++;
        }
    }

    return 0;
}

/*
 * Reads an already-parsed filter array. A filter's condition is kept as a tree so the renderer can
 * fold it into the synthesized query's WHERE clause: the filter is part of the question, and a
 * question missing its filter is a different question.
 *
 * A filter entry with no condition constrains nothing (Power BI keeps the field binding for the UI
 * even when no values are selected), so it contributes no predicate and is not an error.
 *
 * Shared by both report formats: one stores this array as a JSON string (see `parse_filters`), the
 * newer one stores it as a native array under `filterConfig`, but the entries are identical.
 */
static int parse_filters_array(
    json_t *root, ReportFilter **filters, size_t *count, ExprStatus *status)
{
    size_t index;
    json_t *entry;
    int result = 0;

    if (!json_is_array(root)) {
        return 0;
    }

    json_array_foreach(root, index, entry) {
        const char *name = json_text(entry, "name");
        json_t *body = json_object_get(entry, "filter");
        json_t *where;
        QueryExpr *condition = NULL;
        ReportFilter *filter;
        QuerySource *from = NULL;
        size_t from_count = 0;
        size_t where_index;
        json_t *clause;

        if (!json_is_object(body)) {
            continue;
        }

        if (parse_sources(json_object_get(body, "From"), &from, &from_count, status) != 0) {
            query_source_free(from, from_count);
            result = -1;
            goto done;
        }

        where = json_object_get(body, "Where");
        if (json_is_array(where)) {
            json_array_foreach(where, where_index, clause) {
                json_t *condition_node = json_object_get(clause, "Condition");
                QueryExpr *parsed;

                if (!json_is_object(condition_node)) {
                    continue;
                }

                parsed = parse_expression(condition_node, status);
                if (parsed == NULL) {
                    query_expr_free(condition);
                    query_source_free(from, from_count);
                    result = -1;
                    goto done;
                }

                if (condition == NULL) {
                    condition = parsed;
                } else {
                    /* Several Where entries are ANDed, exactly as separate predicates would be. */
                    QueryExpr *combined = expr_new(EXPR_LOGICAL, status);

                    if (combined == NULL) {
                        query_expr_free(parsed);
                        query_expr_free(condition);
                        query_source_free(from, from_count);
                        result = -1;
                        goto done;
                    }
                    combined->left = condition;
                    combined->right = parsed;
                    combined->is_or = 0;
                    condition = combined;
                }
            }
        }

        if (condition == NULL) {
            query_source_free(from, from_count);
            continue;
        }

        filter = (ReportFilter *)realloc(*filters, (*count + 1) * sizeof(*filter));
        if (filter == NULL) {
            query_expr_free(condition);
            query_source_free(from, from_count);
            status->out_of_memory = 1;
            result = -1;
            goto done;
        }
        *filters = filter;
        filter = &(*filters)[*count];
        memset(filter, 0, sizeof(*filter));
        filter->name = strdup(name != NULL ? name : "filter");
        filter->from = from;
        filter->from_count = from_count;
        filter->condition = condition;
        (*count)++;
        if (filter->name == NULL) {
            status->out_of_memory = 1;
            result = -1;
            goto done;
        }
    }

done:
    return result;
}

/*
 * Reads a filter array stored as a JSON string inside the outer JSON, the form the single-part
 * `Report/Layout` uses. The entries themselves are read by `parse_filters_array`.
 */
static int parse_filters(
    const char *filters_text, ReportFilter **filters, size_t *count, ExprStatus *status)
{
    json_error_t json_error;
    json_t *root;
    int result;

    if (filters_text == NULL || filters_text[0] == '\0') {
        return 0;
    }

    root = json_loads(filters_text, 0, &json_error);
    if (root == NULL) {
        /* Power BI writes this member as a string holding JSON; text that does not parse carries no
         * recoverable condition, and treating it as "no filter" would widen the question. */
        set_refusal(status, "a filter list that is not valid JSON (line %d: %s)",
            json_error.line, json_error.text);
        return -1;
    }

    result = parse_filters_array(root, filters, count, status);
    json_decref(root);
    return result;
}

/* Releases everything one visual owns, leaving it zeroed. */
static void free_visual(ReportVisual *visual)
{
    size_t i;

    for (i = 0; i < visual->field_count; i++) {
        free(visual->fields[i].role);
        free(visual->fields[i].query_ref);
        free(visual->fields[i].table);
        free(visual->fields[i].column_or_measure);
    }
    free(visual->fields);
    free(visual->visual_type);
    free(visual->title);
    free(visual->sql);
    visual_query_free(&visual->query);
    report_filters_free(visual->filters, visual->filter_count);
    memset(visual, 0, sizeof(*visual));
}

/* Appends a warning, taking ownership of nothing: the text is copied. */
static int add_warning(ReportLayout *layout, const char *format, ...)
    __attribute__((format(printf, 2, 3)));

static int add_warning(ReportLayout *layout, const char *format, ...)
{
    char text[512];
    char **grown;
    va_list args;

    va_start(args, format);
    vsnprintf(text, sizeof(text), format, args);
    va_end(args);

    grown = (char **)realloc(layout->warnings, (layout->warning_count + 1) * sizeof(*grown));
    if (grown == NULL) {
        return -1;
    }
    layout->warnings = grown;
    layout->warnings[layout->warning_count] = strdup(text);
    if (layout->warnings[layout->warning_count] == NULL) {
        return -1;
    }
    layout->warning_count++;
    return 0;
}

/*
 * Resolves a selected expression to the table and column/measure it names.
 *
 * An aggregation is unwrapped to the field it aggregates ("sum of Sales Amount by month" is still
 * a question about Sales Amount), and a hierarchy level resolves to its level, which is the field
 * the visual actually groups by ("Month", not the "Fiscal" hierarchy containing it).
 *
 * Returns 0 on success and fills the out-parameters (pointers into the JSON, valid while the
 * document is), or -1 when the expression names no field at all.
 */
static int resolve_field(
    json_t *expression, json_t *sources_by_alias,
    const char **table, const char **name, int *is_measure)
{
    json_t *node;
    const char *keys[] = { "Column", "Measure", "HierarchyLevel" };
    size_t i;

    /* An aggregation wraps the real field. */
    node = json_object_get(expression, "Aggregation");
    if (json_is_object(node)) {
        json_t *inner = json_object_get(node, "Expression");

        return json_is_object(inner)
            ? resolve_field(inner, sources_by_alias, table, name, is_measure)
            : -1;
    }

    for (i = 0; i < sizeof(keys) / sizeof(keys[0]); i++) {
        json_t *source_ref;
        json_t *reference;
        const char *alias;
        const char *entity;

        node = json_object_get(expression, keys[i]);
        if (!json_is_object(node)) {
            continue;
        }

        *is_measure = (strcmp(keys[i], "Measure") == 0);

        if (strcmp(keys[i], "HierarchyLevel") == 0) {
            /* A hierarchy level's own source sits one level deeper, under its Hierarchy. */
            json_t *hierarchy_expression = json_object_get(node, "Expression");
            json_t *hierarchy = hierarchy_expression != NULL
                ? json_object_get(hierarchy_expression, "Hierarchy") : NULL;

            *name = json_text(node, "Level");
            reference = hierarchy;
        } else {
            *name = json_text(node, "Property");
            reference = node;
        }

        if (*name == NULL || reference == NULL) {
            return -1;
        }

        source_ref = json_object_get(reference, "Expression");
        source_ref = source_ref != NULL ? json_object_get(source_ref, "SourceRef") : NULL;
        if (!json_is_object(source_ref)) {
            return -1;
        }

        /* A reference names either an alias bound by the query's FROM, or an entity directly. */
        alias = json_text(source_ref, "Source");
        entity = json_text(source_ref, "Entity");

        if (alias != NULL) {
            const char *bound = json_text(sources_by_alias, alias);

            if (bound == NULL) {
                return -1;
            }
            *table = bound;
            return 0;
        }
        if (entity != NULL) {
            *table = entity;
            return 0;
        }
        return -1;
    }

    return -1;
}

/*
 * Reads one visual container, appending it to the page when it carries a question.
 *
 * A visual that projects no field is decoration: a shape, a textbox, an image. It states no
 * business question, so it is deliberately not recorded rather than stored as an empty visual.
 */
static int read_visual(
    json_t *container, int ordinal, ReportPage *page, ReportLayout *layout,
    char *error, size_t error_size)
{
    const char *config_text = json_text(container, "config");
    json_error_t json_error;
    json_t *config;
    json_t *single_visual;
    json_t *projections;
    json_t *prototype_query;
    json_t *select;
    json_t *from;
    json_t *selections_by_name;
    json_t *sources_by_alias;
    json_t *role_value;
    const char *role_name;
    ReportVisual *visual;
    ExprStatus expr_status;
    char refusal[256];
    const char *visual_type;
    const char *page_label;
    size_t index;
    json_t *entry;
    int render_result;
    int status = -1;

    if (config_text == NULL) {
        return 0;
    }

    /* The container's config is JSON stored as a string inside the outer JSON. */
    config = json_loads(config_text, 0, &json_error);
    if (config == NULL) {
        set_errorf(error, error_size,
            "a visual's configuration is not valid JSON (line %d: %s)",
            json_error.line, json_error.text);
        return -1;
    }

    single_visual = json_object_get(config, "singleVisual");
    if (!json_is_object(single_visual)) {
        /* A grouped container holds no query of its own; its children appear as their own
         * entries in the page's container list. */
        json_decref(config);
        return 0;
    }

    projections = json_object_get(single_visual, "projections");
    prototype_query = json_object_get(single_visual, "prototypeQuery");

    if (!json_is_object(projections) || json_object_size(projections) == 0
        || !json_is_object(prototype_query)) {
        json_decref(config);
        return 0;
    }

    select = json_object_get(prototype_query, "Select");
    from = json_object_get(prototype_query, "From");
    if (!json_is_array(select)) {
        json_decref(config);
        return 0;
    }

    /* Index the query's selections by the name a projection's queryRef points at, and its FROM
     * bindings by alias. Together these turn a projection into a concrete table and column. */
    selections_by_name = json_object();
    sources_by_alias = json_object();
    if (selections_by_name == NULL || sources_by_alias == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        goto done;
    }

    json_array_foreach(select, index, entry) {
        const char *name = json_text(entry, "Name");

        if (name != NULL) {
            json_object_set(selections_by_name, name, entry);
        }
    }

    if (json_is_array(from)) {
        json_array_foreach(from, index, entry) {
            const char *alias = json_text(entry, "Name");
            const char *entity = json_text(entry, "Entity");

            if (alias != NULL && entity != NULL) {
                json_object_set_new(sources_by_alias, alias, json_string(entity));
            }
        }
    }

    visual = (ReportVisual *)push_row(
        (void **)&page->visuals, &page->visual_count, sizeof(*page->visuals));
    if (visual == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        goto done;
    }

    visual->ordinal = ordinal;
    visual->visual_type = dup_text(single_visual, "visualType");
    if (visual->visual_type == NULL) {
        visual->visual_type = strdup("unknown");
    }
    visual->title = read_title(single_visual, "vcObjects");

    visual_type = visual->visual_type != NULL ? visual->visual_type : "unknown";
    page_label = page->display_name != NULL ? page->display_name : "";

    /* The query and the filters are kept as trees so the visual's question can be rendered as one
     * SELECT. Both are parsed before any field is resolved, because a refusal in either drops the
     * whole visual and there is no point resolving fields for a visual that will not be kept. */
    memset(&expr_status, 0, sizeof(expr_status));
    if (parse_query(prototype_query, &visual->query, &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a visual's query");
            goto done;
        }
        if (add_warning(layout,
                "page '%s' visual #%d (%s) uses a query expression the extractor does not support "
                "(%s); the visual was skipped rather than recorded with an incomplete query.",
                page_label, ordinal, visual_type, expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    if (parse_filters(json_text(container, "filters"), &visual->filters, &visual->filter_count,
            &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a visual's filters");
            goto done;
        }
        /* A filter that cannot be translated must not be dropped quietly: without it the
         * synthesized query is BROADER than the question the visual actually asks, which is the one
         * failure mode that would make a confirmed example wrong. Refusing the visual is honest. */
        if (add_warning(layout,
                "page '%s' visual #%d (%s) has a filter using an unsupported expression (%s); the "
                "visual was skipped, because recording it without the filter would widen the "
                "question it asks.",
                page_label, ordinal, visual_type, expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    /* Each projection bucket is a ROLE, and its name is kept verbatim: Category/Y for a chart,
     * Rows/Values for a matrix, Size for a map. The role is the author's own statement of the
     * question's shape. */
    json_object_foreach(projections, role_name, role_value) {
        if (!json_is_array(role_value)) {
            continue;
        }

        json_array_foreach(role_value, index, entry) {
            const char *query_ref = json_text(entry, "queryRef");
            json_t *selection;
            const char *table = NULL;
            const char *name = NULL;
            int is_measure = 0;
            VisualField *field;

            if (query_ref == NULL) {
                continue;
            }

            selection = json_object_get(selections_by_name, query_ref);
            if (!json_is_object(selection)) {
                /* The projection names a field the query does not select, so it cannot be
                 * resolved to a table; skipping it beats recording a field with no identity. */
                if (add_warning(layout,
                        "page '%s' visual #%d (%s) projects '%s' in role '%s', which its query does "
                        "not select; that field was skipped.",
                        page_label, ordinal, visual_type, query_ref, role_name) != 0) {
                    set_error(error, error_size, "out of memory recording a warning");
                    goto done;
                }
                continue;
            }

            if (resolve_field(selection, sources_by_alias, &table, &name, &is_measure) != 0
                || table == NULL || name == NULL) {
                if (add_warning(layout,
                        "page '%s' visual #%d (%s) projects '%s' in role '%s', which resolves to no "
                        "column or measure; that field was skipped.",
                        page_label, ordinal, visual_type, query_ref, role_name) != 0) {
                    set_error(error, error_size, "out of memory recording a warning");
                    goto done;
                }
                continue;
            }

            field = (VisualField *)push_row(
                (void **)&visual->fields, &visual->field_count, sizeof(*visual->fields));
            if (field == NULL) {
                set_error(error, error_size, "out of memory reading a visual's fields");
                goto done;
            }

            field->role = strdup(role_name);
            field->query_ref = strdup(query_ref);
            field->table = strdup(table);
            field->column_or_measure = strdup(name);
            field->is_measure = is_measure;
        }
    }

    /* Having resolved nothing, the visual states no question after all. Drop it rather than
     * emit a visual with an empty field list. */
    if (visual->field_count == 0) {
        if (add_warning(layout,
                "page '%s' visual #%d (%s) projects fields but none resolved to a column or "
                "measure; it was skipped.",
                page_label, ordinal, visual_type) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    /* The page's own filters narrow every visual on it, so they are folded in here rather than
     * left for a consumer to remember to apply. */
    render_result = sql_render_visual(
        visual, page->filters, page->filter_count, &visual->sql, refusal, sizeof(refusal));
    if (render_result < 0) {
        set_error(error, error_size, "out of memory rendering a visual's query");
        goto done;
    }
    if (render_result > 0) {
        if (add_warning(layout,
                "page '%s' visual #%d (%s) has a filter using an unsupported expression (%s); the "
                "visual was skipped, because recording it without the filter would widen the "
                "question it asks.",
                page_label, ordinal, visual_type, refusal) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    status = 0;
    goto done;

drop_visual:
    free_visual(visual);
    page->visual_count--;
    status = 0;

done:
    json_decref(selections_by_name);
    json_decref(sources_by_alias);
    json_decref(config);
    return status;
}

static int read_page(json_t *section, int ordinal, ReportLayout *layout,
    char *error, size_t error_size)
{
    ReportPage *page;
    json_t *containers;
    size_t index;
    json_t *container;
    ExprStatus expr_status;
    int visual_ordinal = 0;

    page = (ReportPage *)push_row(
        (void **)&layout->pages, &layout->page_count, sizeof(*layout->pages));
    if (page == NULL) {
        set_error(error, error_size, "out of memory reading the report's pages");
        return -1;
    }

    page->ordinal = ordinal;
    page->name = dup_text(section, "name");
    page->display_name = dup_text(section, "displayName");
    if (page->display_name == NULL && page->name != NULL) {
        page->display_name = strdup(page->name);
    }

    /* A page-level filter (a slicer, say) narrows every visual on the page, so it is read once here
     * and folded into each visual's rendered query. A page filter that cannot be represented is
     * refused for the page rather than silently ignored, since ignoring it would widen every
     * question on the page at once. */
    memset(&expr_status, 0, sizeof(expr_status));
    if (parse_filters(json_text(section, "filters"), &page->filters, &page->filter_count,
            &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a page's filters");
            return -1;
        }
        if (add_warning(layout,
                "page '%s' has a filter using an unsupported expression (%s); every visual on it "
                "was skipped, because recording them without the filter would widen the questions "
                "they ask.",
                page->display_name != NULL ? page->display_name : "", expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            return -1;
        }
        return 0;
    }

    containers = json_object_get(section, "visualContainers");
    if (!json_is_array(containers)) {
        return 0;
    }

    json_array_foreach(containers, index, container) {
        visual_ordinal++;
        if (read_visual(container, visual_ordinal, page, layout, error, error_size) != 0) {
            return -1;
        }
    }

    return 0;
}

/* ---- The split report format (Report/definition/...) ------------------------------------- */

/*
 * Power BI Desktop now writes a report as a tree of small JSON documents rather than one
 * `Report/Layout` blob: `Report/definition/pages/pages.json` orders the pages, each page is
 * `.../pages/<page>/page.json`, and each visual on it is `.../visuals/<id>/visual.json`.
 *
 * The documents are plain UTF-8 JSON, and the expression vocabulary inside them (Column, Measure,
 * HierarchyLevel, Aggregation, In, Comparison, ...) is byte-for-byte the one the older format uses,
 * so every expression, filter and field parser above is reused verbatim. What differs is only the
 * shape AROUND those expressions, which is what these functions translate:
 *
 *   - a visual's fields live in `visual.query.queryState.<role>.projections[].field`, which holds
 *     the expression INLINE, rather than a `queryRef` pointing into a separate `prototypeQuery`;
 *   - there is no query-level `From`, because references name their entity directly rather than an
 *     alias, so the FROM clause is reconstructed from the entities the fields actually name;
 *   - filters sit in a native `filterConfig.filters` array instead of a stringified one;
 *   - ordering sits in `visual.query.sortDefinition.sort[]` with a spelled-out direction.
 */

/* Reads a member of the archive as UTF-8 JSON. Returns NULL with `error` set. */
static json_t *read_json_member(
    const char *pbix_path, const char *member_name, char *error, size_t error_size)
{
    Buffer member;
    json_error_t json_error;
    json_t *root;

    buffer_init(&member);
    if (pbix_read_member(pbix_path, member_name, &member, error, error_size) != 0) {
        buffer_free(&member);
        return NULL;
    }

    /* These documents are UTF-8 and carry no terminator of their own, so the size is given
     * explicitly rather than relying on one. */
    root = json_loadb((const char *)member.data, member.size, 0, &json_error);
    if (root == NULL) {
        set_errorf(error, error_size, "'%s' is not valid JSON (line %d: %s)",
            member_name, json_error.line, json_error.text);
    }
    buffer_free(&member);
    return root;
}

/*
 * Binds an entity into the query's FROM, once per distinct entity.
 *
 * The split format drops the query-level FROM because its references are already entity-qualified,
 * but the rendered SQL still has to say which tables the visual reads: that list is the visual's
 * consumption lineage. The alias is the entity's own name, which is what the field references use.
 */
static int bind_entity(VisualQuery *query, const char *entity)
{
    size_t i;
    QuerySource *grown;

    if (entity == NULL || entity[0] == '\0') {
        return 0;
    }
    for (i = 0; i < query->from_count; i++) {
        if (strcmp(query->from[i].entity, entity) == 0) {
            return 0;
        }
    }

    grown = (QuerySource *)realloc(query->from, (query->from_count + 1) * sizeof(*grown));
    if (grown == NULL) {
        return -1;
    }
    query->from = grown;
    query->from[query->from_count].alias = strdup(entity);
    query->from[query->from_count].entity = strdup(entity);
    query->from_count++;
    if (query->from[query->from_count - 1].alias == NULL
        || query->from[query->from_count - 1].entity == NULL) {
        return -1;
    }
    return 0;
}

/* Reads `sortDefinition.sort[]` into the query's ORDER BY. */
static int parse_sort_definition(json_t *query_node, VisualQuery *query, ExprStatus *status)
{
    json_t *sort_definition = json_object_get(query_node, "sortDefinition");
    json_t *sort;
    size_t index;
    json_t *entry;

    if (!json_is_object(sort_definition)) {
        return 0;
    }
    sort = json_object_get(sort_definition, "sort");
    if (!json_is_array(sort)) {
        return 0;
    }

    json_array_foreach(sort, index, entry) {
        json_t *field = json_object_get(entry, "field");
        const char *direction = json_text(entry, "direction");
        QueryOrdering *grown;
        QueryExpr *parsed;

        if (!json_is_object(field)) {
            continue;
        }

        parsed = parse_expression(field, status);
        if (parsed == NULL) {
            return -1;
        }

        grown = (QueryOrdering *)realloc(query->order_by,
            (query->order_by_count + 1) * sizeof(*grown));
        if (grown == NULL) {
            query_expr_free(parsed);
            status->out_of_memory = 1;
            return -1;
        }
        query->order_by = grown;
        query->order_by[query->order_by_count].expression = parsed;
        query->order_by[query->order_by_count].descending =
            direction != NULL && strcmp(direction, "Descending") == 0;
        query->order_by_count++;
    }

    return 0;
}

/*
 * Reads one visual.json, appending it to the page when it carries a question.
 *
 * As in the older format, a visual that projects no field states no business question (a textbox, a
 * shape, an image) and is deliberately not recorded rather than stored empty.
 */
static int read_visual_split(
    json_t *document, int ordinal, ReportPage *page, ReportLayout *layout,
    char *error, size_t error_size)
{
    json_t *single_visual = json_object_get(document, "visual");
    json_t *query_node;
    json_t *query_state;
    json_t *filter_config;
    json_t *role_value;
    const char *role_name;
    ReportVisual *visual;
    ExprStatus expr_status;
    char refusal[256];
    const char *visual_type;
    const char *page_label;
    json_t *empty_aliases;
    size_t index;
    json_t *entry;
    int render_result;
    int status = -1;

    if (!json_is_object(single_visual)) {
        return 0;
    }

    query_node = json_object_get(single_visual, "query");
    query_state = json_is_object(query_node) ? json_object_get(query_node, "queryState") : NULL;
    if (!json_is_object(query_state) || json_object_size(query_state) == 0) {
        return 0;
    }

    /* References in this format are entity-qualified rather than alias-bound, so there are no alias
     * bindings to resolve against; `resolve_field` falls through to the entity on an empty map. */
    empty_aliases = json_object();
    if (empty_aliases == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        return -1;
    }

    visual = (ReportVisual *)push_row(
        (void **)&page->visuals, &page->visual_count, sizeof(*page->visuals));
    if (visual == NULL) {
        set_error(error, error_size, "out of memory reading a visual");
        goto done;
    }

    visual->ordinal = ordinal;
    visual->visual_type = dup_text(single_visual, "visualType");
    if (visual->visual_type == NULL) {
        visual->visual_type = strdup("unknown");
    }
    visual->title = read_title(single_visual, "visualContainerObjects");

    visual_type = visual->visual_type != NULL ? visual->visual_type : "unknown";
    page_label = page->display_name != NULL ? page->display_name : "";

    memset(&expr_status, 0, sizeof(expr_status));

    /* Each projection bucket is a ROLE, and its name is kept verbatim: Category/Y for a chart,
     * Rows/Values for a matrix, Size for a map. Unlike the older format the field's expression is
     * inline, so it is both parsed for the query and resolved for the field list from one node. */
    json_object_foreach(query_state, role_name, role_value) {
        json_t *projections = json_is_object(role_value)
            ? json_object_get(role_value, "projections") : NULL;

        if (!json_is_array(projections)) {
            continue;
        }

        json_array_foreach(projections, index, entry) {
            const char *query_ref = json_text(entry, "queryRef");
            json_t *field_node = json_object_get(entry, "field");
            const char *table = NULL;
            const char *name = NULL;
            int is_measure = 0;
            VisualField *field;
            QuerySelection *grown;
            QueryExpr *parsed;

            if (query_ref == NULL || !json_is_object(field_node)) {
                continue;
            }

            if (resolve_field(field_node, empty_aliases, &table, &name, &is_measure) != 0
                || table == NULL || name == NULL) {
                if (add_warning(layout,
                        "page '%s' visual #%d (%s) projects '%s' in role '%s', which resolves to no "
                        "column or measure; that field was skipped.",
                        page_label, ordinal, visual_type, query_ref, role_name) != 0) {
                    set_error(error, error_size, "out of memory recording a warning");
                    goto done;
                }
                continue;
            }

            parsed = parse_expression(field_node, &expr_status);
            if (parsed == NULL) {
                if (expr_status.out_of_memory) {
                    set_error(error, error_size, "out of memory reading a visual's query");
                    goto done;
                }
                if (add_warning(layout,
                        "page '%s' visual #%d (%s) uses a query expression the extractor does not "
                        "support (%s); the visual was skipped rather than recorded with an "
                        "incomplete query.",
                        page_label, ordinal, visual_type, expr_status.reason) != 0) {
                    set_error(error, error_size, "out of memory recording a warning");
                    goto done;
                }
                goto drop_visual;
            }

            grown = (QuerySelection *)realloc(visual->query.select,
                (visual->query.select_count + 1) * sizeof(*grown));
            if (grown == NULL) {
                query_expr_free(parsed);
                set_error(error, error_size, "out of memory reading a visual's query");
                goto done;
            }
            visual->query.select = grown;
            visual->query.select[visual->query.select_count].name = strdup(query_ref);
            visual->query.select[visual->query.select_count].expression = parsed;
            visual->query.select_count++;
            if (visual->query.select[visual->query.select_count - 1].name == NULL
                || bind_entity(&visual->query, table) != 0) {
                set_error(error, error_size, "out of memory reading a visual's query");
                goto done;
            }

            field = (VisualField *)push_row(
                (void **)&visual->fields, &visual->field_count, sizeof(*visual->fields));
            if (field == NULL) {
                set_error(error, error_size, "out of memory reading a visual's fields");
                goto done;
            }

            field->role = strdup(role_name);
            field->query_ref = strdup(query_ref);
            field->table = strdup(table);
            field->column_or_measure = strdup(name);
            field->is_measure = is_measure;
            if (field->role == NULL || field->query_ref == NULL || field->table == NULL
                || field->column_or_measure == NULL) {
                set_error(error, error_size, "out of memory reading a visual's fields");
                goto done;
            }
        }
    }

    if (visual->field_count == 0) {
        goto drop_visual;
    }

    if (parse_sort_definition(query_node, &visual->query, &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a visual's ordering");
            goto done;
        }
        if (add_warning(layout,
                "page '%s' visual #%d (%s) orders by an expression the extractor does not support "
                "(%s); the visual was skipped rather than recorded with an incomplete query.",
                page_label, ordinal, visual_type, expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    filter_config = json_object_get(single_visual, "filterConfig");
    if (json_is_object(filter_config)
        && parse_filters_array(json_object_get(filter_config, "filters"), &visual->filters,
            &visual->filter_count, &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a visual's filters");
            goto done;
        }
        /* Same reasoning as the older format: a filter that cannot be translated must not be
         * dropped quietly, because without it the synthesized query is BROADER than the question
         * the visual actually asks. */
        if (add_warning(layout,
                "page '%s' visual #%d (%s) has a filter using an unsupported expression (%s); the "
                "visual was skipped, because recording it without the filter would widen the "
                "question it asks.",
                page_label, ordinal, visual_type, expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    render_result = sql_render_visual(
        visual, page->filters, page->filter_count, &visual->sql, refusal, sizeof(refusal));
    if (render_result < 0) {
        set_error(error, error_size, "out of memory rendering a visual's query");
        goto done;
    }
    if (render_result > 0) {
        if (add_warning(layout,
                "page '%s' visual #%d (%s) has a filter using an unsupported expression (%s); the "
                "visual was skipped, because recording it without the filter would widen the "
                "question it asks.",
                page_label, ordinal, visual_type, refusal) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        goto drop_visual;
    }

    status = 0;
    goto done;

drop_visual:
    free_visual(visual);
    page->visual_count--;
    status = 0;

done:
    json_decref(empty_aliases);
    return status;
}

/* Orders a page's visual members, so a report's visuals are numbered stably across runs. */
static int compare_names(const void *left, const void *right)
{
    return strcmp(*(const char *const *)left, *(const char *const *)right);
}

/* Reads one page directory: its page.json, then every visual.json beneath it. */
static int read_page_split(
    const char *pbix_path, const char *page_name, int ordinal, ReportLayout *layout,
    char *error, size_t error_size)
{
    char member_name[1024];
    char prefix[1024];
    json_t *document;
    json_t *filter_config;
    ReportPage *page;
    ExprStatus expr_status;
    char **visual_members = NULL;
    size_t visual_count = 0;
    size_t index;
    int visual_ordinal = 0;
    int status = -1;

    snprintf(member_name, sizeof(member_name),
        "Report/definition/pages/%s/page.json", page_name);
    document = read_json_member(pbix_path, member_name, error, error_size);
    if (document == NULL) {
        return -1;
    }

    page = (ReportPage *)push_row(
        (void **)&layout->pages, &layout->page_count, sizeof(*layout->pages));
    if (page == NULL) {
        set_error(error, error_size, "out of memory reading the report's pages");
        goto done;
    }

    page->ordinal = ordinal;
    page->name = dup_text(document, "name");
    page->display_name = dup_text(document, "displayName");
    if (page->name == NULL) {
        page->name = strdup(page_name);
    }
    if (page->display_name == NULL && page->name != NULL) {
        page->display_name = strdup(page->name);
    }

    /* A page-level filter narrows every visual on the page, so it is read before them and folded
     * into each one's rendered query. Refusing it for the whole page beats ignoring it, which would
     * widen every question on the page at once. */
    memset(&expr_status, 0, sizeof(expr_status));
    filter_config = json_object_get(document, "filterConfig");
    if (json_is_object(filter_config)
        && parse_filters_array(json_object_get(filter_config, "filters"), &page->filters,
            &page->filter_count, &expr_status) != 0) {
        if (expr_status.out_of_memory) {
            set_error(error, error_size, "out of memory reading a page's filters");
            goto done;
        }
        if (add_warning(layout,
                "page '%s' has a filter using an unsupported expression (%s); every visual on it "
                "was skipped, because recording them without the filter would widen the questions "
                "they ask.",
                page->display_name != NULL ? page->display_name : "", expr_status.reason) != 0) {
            set_error(error, error_size, "out of memory recording a warning");
            goto done;
        }
        status = 0;
        goto done;
    }

    /* The visuals are one member each under a generated id, so they are enumerated rather than
     * named, then sorted: the archive's order is not meaningful, and a stable order keeps a
     * visual's ordinal the same from one extraction to the next. */
    snprintf(prefix, sizeof(prefix), "Report/definition/pages/%s/visuals/", page_name);
    if (pbix_list_members(pbix_path, prefix, "/visual.json", &visual_members, &visual_count,
            error, error_size) != 0) {
        goto done;
    }
    qsort(visual_members, visual_count, sizeof(*visual_members), compare_names);

    for (index = 0; index < visual_count; index++) {
        json_t *visual_document = read_json_member(
            pbix_path, visual_members[index], error, error_size);

        if (visual_document == NULL) {
            goto done;
        }
        visual_ordinal++;
        if (read_visual_split(visual_document, visual_ordinal, page, layout, error, error_size)
            != 0) {
            json_decref(visual_document);
            goto done;
        }
        json_decref(visual_document);
    }

    status = 0;

done:
    pbix_member_names_free(visual_members, visual_count);
    json_decref(document);
    return status;
}

/* Reads a report stored in the split format, page order taken from pages.json. */
static int report_layout_read_split(
    const char *pbix_path, ReportLayout *layout, char *error, size_t error_size)
{
    json_t *document = read_json_member(
        pbix_path, "Report/definition/pages/pages.json", error, error_size);
    json_t *page_order;
    size_t index;
    json_t *entry;
    int ordinal = 0;
    int status = -1;

    if (document == NULL) {
        return -1;
    }

    page_order = json_object_get(document, "pageOrder");
    if (!json_is_array(page_order)) {
        /* A definition with no page order lists no pages, which is no questions rather than a
         * failure, exactly as an empty `sections` array is in the older format. */
        status = 0;
        goto done;
    }

    json_array_foreach(page_order, index, entry) {
        const char *page_name = json_is_string(entry) ? json_string_value(entry) : NULL;

        if (page_name == NULL || page_name[0] == '\0') {
            continue;
        }
        ordinal++;
        if (read_page_split(pbix_path, page_name, ordinal, layout, error, error_size) != 0) {
            goto done;
        }
    }

    status = 0;

done:
    json_decref(document);
    return status;
}

int report_layout_read(const char *pbix_path, ReportLayout *layout, char *error, size_t error_size)
{
    Buffer member;
    char *json_text_utf8 = NULL;
    json_error_t json_error;
    json_t *root = NULL;
    json_t *sections;
    size_t index;
    json_t *section;
    int ordinal = 0;
    int status = -1;

    memset(layout, 0, sizeof(*layout));
    buffer_init(&member);

    if (pbix_read_member(pbix_path, "Report/Layout", &member, error, error_size) != 0) {
        /* No single-part layout: the report is either in the newer split format or has no visual
         * layer at all. Both are read from `Report/definition`, and only a file with neither part
         * is the failure this reports. */
        buffer_free(&member);
        if (report_layout_read_split(pbix_path, layout, error, error_size) != 0) {
            report_layout_free(layout);
            return -1;
        }
        return 0;
    }

    /* The layout is UTF-16LE, with no byte order mark in the files seen so far. */
    json_text_utf8 = pbix_utf16le_to_utf8(member.data, member.size);
    if (json_text_utf8 == NULL) {
        set_error(error, error_size, "the report layout is not valid UTF-16 text");
        goto done;
    }

    root = json_loads(json_text_utf8, 0, &json_error);
    if (root == NULL) {
        set_errorf(error, error_size,
            "the report layout is not valid JSON (line %d: %s)",
            json_error.line, json_error.text);
        goto done;
    }

    sections = json_object_get(root, "sections");
    if (!json_is_array(sections)) {
        /* A file can hold a model with no report built on it yet. That is not an error; it just
         * means there are no questions to record. */
        status = 0;
        goto done;
    }

    json_array_foreach(sections, index, section) {
        ordinal++;
        if (read_page(section, ordinal, layout, error, error_size) != 0) {
            goto done;
        }
    }

    status = 0;

done:
    if (root != NULL) {
        json_decref(root);
    }
    free(json_text_utf8);
    buffer_free(&member);
    if (status != 0) {
        report_layout_free(layout);
    }
    return status;
}

void report_layout_free(ReportLayout *layout)
{
    size_t page_index;
    size_t warning_index;

    for (page_index = 0; page_index < layout->page_count; page_index++) {
        ReportPage *page = &layout->pages[page_index];
        size_t visual_index;

        for (visual_index = 0; visual_index < page->visual_count; visual_index++) {
            free_visual(&page->visuals[visual_index]);
        }
        free(page->visuals);
        free(page->name);
        free(page->display_name);
        report_filters_free(page->filters, page->filter_count);
    }
    free(layout->pages);

    for (warning_index = 0; warning_index < layout->warning_count; warning_index++) {
        free(layout->warnings[warning_index]);
    }
    free(layout->warnings);

    memset(layout, 0, sizeof(*layout));
}
