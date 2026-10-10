/*
 * Reads a .pbix's VISUAL layer: the pages a person flips through, the visuals on each, the role
 * every field plays in them, and the query each visual runs.
 *
 * This is the other half of what a report knows, and the more direct half. The semantic model
 * (see metadata.h) says what CAN be asked; the visual layer is a record of what people actually
 * asked and considered worth putting on a page. A chart titled "Sales Amount by Category" is a
 * business question whose shape is already settled, and the role each field sits in -- the axis a
 * chart is broken down BY versus the value it plots -- is the part of that shape which no amount
 * of reading the model or the warehouse schema recovers.
 *
 * Unlike the model, this needs no decompression: the `Report/Layout` part is plain UTF-16LE JSON
 * inside the zip. Two of Power BI's habits still have to be undone. Several fields hold JSON as a
 * STRING inside the outer JSON (a visual's `config`, and both filter lists), so they are parsed a
 * second time; and text literals are stored already single-quoted ('Revenue by Region').
 */

#ifndef PBIX_REPORTLAYOUT_H
#define PBIX_REPORTLAYOUT_H

#include <stddef.h>

/*
 * One node of a Power BI query expression.
 *
 * A visual's query (`prototypeQuery`) and a filter's condition are written in the SAME node
 * vocabulary, which is why one tree type and one renderer serve both: a filter is not a different
 * kind of thing from the query it constrains, it is more of the same expression language.
 *
 * The set is closed on purpose. Power BI's expression language is larger than this, but an
 * unrecognized node cannot be silently dropped: dropping part of a filter would WIDEN the question
 * (a query returning more rows than the visual shows) and dropping part of a selection would lose a
 * field. The parser therefore refuses what it cannot represent, and the refusal is reported as a
 * warning naming the visual, rather than producing a query that is quietly wrong.
 */
typedef enum {
    EXPR_COLUMN,
    EXPR_MEASURE,
    EXPR_HIERARCHY_LEVEL,
    EXPR_AGGREGATION,
    EXPR_LITERAL,
    EXPR_IN,
    EXPR_NOT,
    EXPR_LOGICAL,
    EXPR_COMPARISON
} ExprKind;

/* The aggregate functions Power BI encodes numerically in a visual query. */
typedef enum {
    AGGREGATE_SUM,
    AGGREGATE_AVERAGE,
    AGGREGATE_COUNT,
    AGGREGATE_MIN,
    AGGREGATE_MAX,
    AGGREGATE_COUNT_NON_NULL
} AggregateFunction;

/* The comparison operators Power BI encodes numerically in a condition. */
typedef enum {
    COMPARISON_EQUAL,
    COMPARISON_NOT_EQUAL,
    COMPARISON_GREATER_THAN,
    COMPARISON_GREATER_THAN_OR_EQUAL,
    COMPARISON_LESS_THAN,
    COMPARISON_LESS_THAN_OR_EQUAL
} ComparisonOperator;

/*
 * One expression node.
 *
 * OWNERSHIP: every QueryExpr is heap-allocated and owned by its parent, and `query_expr_free`
 * recurses over the whole tree. Every parser in reportlayout.c frees its partially built node
 * before returning on failure, so a half-built tree never escapes to a caller.
 */
typedef struct QueryExpr QueryExpr;
struct QueryExpr {
    ExprKind kind;

    /* COLUMN / MEASURE / HIERARCHY_LEVEL: a reference names EITHER an alias bound by the query's
     * FROM or an entity directly (Power BI allows SourceRef.Entity in place of an alias, notably
     * inside filters), so at most one of these two is set. */
    char *source_alias;
    char *source_entity;

    char *property;         /* COLUMN / MEASURE: the column or measure name */
    char *hierarchy;        /* HIERARCHY_LEVEL: the hierarchy containing the level */
    char *level;            /* HIERARCHY_LEVEL: the level, which is the field grouped by */
    char *literal;          /* LITERAL: the value verbatim, including Power BI's own quoting */

    AggregateFunction function;     /* AGGREGATION */
    ComparisonOperator comparison;  /* COMPARISON */
    int is_or;                      /* LOGICAL: 1 for OR, 0 for AND */

    QueryExpr *inner;       /* AGGREGATION / NOT: the single operand */
    QueryExpr *left;        /* LOGICAL / COMPARISON */
    QueryExpr *right;       /* LOGICAL / COMPARISON */

    /* IN: `in_expression_count` expressions tested against `in_value_count` value tuples. Each
     * tuple is exactly `in_expression_count` wide, so `in_values` is a rectangular array of rows
     * addressed as in_values[row][column]. */
    QueryExpr **in_expressions;
    size_t in_expression_count;
    QueryExpr ***in_values;
    size_t in_value_count;
};

/* An entity bound by a query's FROM, under the alias its expressions reference it by. */
typedef struct {
    char *alias;
    char *entity;
} QuerySource;

/* One projected expression, under the name a visual's projections point at via queryRef. */
typedef struct {
    char *name;
    QueryExpr *expression;
} QuerySelection;

typedef struct {
    QueryExpr *expression;
    int descending;
} QueryOrdering;

/* A visual's query: what it reads, what it projects, and in what order. */
typedef struct {
    QuerySource *from;
    size_t from_count;
    QuerySelection *select;
    size_t select_count;
    QueryOrdering *order_by;
    size_t order_by_count;
} VisualQuery;

/*
 * One filter narrowing a question, carrying its own FROM bindings so its aliases resolve even when
 * they are not the visual query's.
 *
 * A filter is part of the question: a chart of sales restricted to one business type asks a
 * narrower question than the same chart unrestricted, so the condition is folded into the rendered
 * query's WHERE clause rather than stored apart from it.
 */
typedef struct {
    char *name;
    QuerySource *from;
    size_t from_count;
    QueryExpr *condition;   /* never NULL: a filter with no condition is not recorded at all */
} ReportFilter;

/* One field or measure a visual projects, and the role it plays in the question. */
typedef struct {
    char *role;             /* Power BI's projection bucket: Category, Y, Rows, Values, Size, ... */
    char *query_ref;        /* the field's reference name within the visual's query */
    char *table;            /* the entity the field belongs to, as the visual's query named it */
    char *column_or_measure;
    int is_measure;         /* a model-computed measure rather than a stored column */
} VisualField;

/* One visual on a page. Only visuals that project at least one field are represented. */
typedef struct {
    int ordinal;            /* 1-based position within its page */
    char *visual_type;      /* areaChart, pivotTable, slicer, map, ... */
    char *title;            /* the authored title, or NULL when the visual has none */
    VisualField *fields;
    size_t field_count;
    VisualQuery query;
    ReportFilter *filters;
    size_t filter_count;
    char *sql;              /* the visual's question rendered as one T-SQL SELECT */
} ReportVisual;

/* One page of the report. */
typedef struct {
    int ordinal;            /* 1-based position within the report */
    char *name;             /* the page's internal identifier */
    char *display_name;     /* the page's title as a person sees it on the tab */
    ReportVisual *visuals;
    size_t visual_count;
    ReportFilter *filters;  /* page-level filters, which narrow EVERY visual on the page */
    size_t filter_count;
} ReportPage;

typedef struct {
    ReportPage *pages;
    size_t page_count;
    /* What was skipped and why: a refused visual, a projection the query does not select. These
     * travel to the caller so a dropped question is visible rather than silently missing. */
    char **warnings;
    size_t warning_count;
} ReportLayout;

/*
 * Reads the visual layer from a .pbix.
 *
 * Returns 0 on success, -1 on failure with a message in `error`. A report with no visuals is a
 * success with zero pages recorded, not a failure: a file can legitimately hold a model and no
 * built report yet. A visual whose query or filter cannot be represented is likewise not a failure:
 * it is dropped, and the reason is recorded in `warnings`.
 */
int report_layout_read(const char *pbix_path, ReportLayout *layout, char *error, size_t error_size);

/* Releases everything the layout owns. Safe on a zeroed layout. */
void report_layout_free(ReportLayout *layout);

/* Releases an expression tree. NULL-safe, and recurses over every owned child. */
void query_expr_free(QueryExpr *expression);

/* Releases a filter array's contents, including each filter's condition tree. */
void report_filters_free(ReportFilter *filters, size_t count);

#endif /* PBIX_REPORTLAYOUT_H */
