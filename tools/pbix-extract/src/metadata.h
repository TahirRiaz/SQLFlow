/*
 * Reads the semantic model's specification out of the SQLite database embedded in a .pbix's
 * decompressed data model.
 *
 * Power BI stores the model's metadata as a SQLite database ("metadata.sqlitedb") inside the
 * Analysis Services backup image. This reads it in place with sqlite3_deserialize, so nothing is
 * written to disk and the queries run against the bytes already in memory.
 *
 * The point of the extraction is to state what the model MEANS: which tables and columns exist,
 * what each measure computes (its DAX), which columns are themselves computed, and how the tables
 * relate. That is the specification a SQL author (or a language model writing SQL) needs, and
 * none of it is present in the report's visual layer.
 */

#ifndef PBIX_METADATA_H
#define PBIX_METADATA_H

#include <stddef.h>

#include "datamodel.h"

typedef struct {
    char *table;
    char *name;
    char *expression;   /* DAX */
    char *description;    /* may be NULL */
} Measure;

typedef struct {
    char *table;
    char *name;
    char *expression;   /* DAX */
} CalculatedColumn;

typedef struct {
    char *from_table;
    char *from_column;
    char *to_table;
    char *to_column;
    char *cardinality;            /* "M:1", "1:1", "1:M", "M:M" */
    int active;
} Relationship;

typedef struct {
    char *table;
    char *column;
    char *data_type;  /* the model's own type name for the column */
} Column;

typedef struct {
    char *table;
    char *expression;   /* Power Query / M */
} TableSource;

/*
 * A shared Power Query expression: a named query the model keeps but does not load as a table
 * (Power BI Desktop's "Enable load" turned off), such as a dimension another table merges in.
 */
typedef struct {
    char *name;
    char *expression;   /* Power Query / M */
} SharedExpression;

typedef struct {
    Measure *measures;
    size_t measure_count;

    CalculatedColumn *calculated_columns;
    size_t calculated_column_count;

    Relationship *relationships;
    size_t relationship_count;

    Column *columns;
    size_t column_count;

    TableSource *sources;
    size_t source_count;

    SharedExpression *expressions;
    size_t expression_count;
} ModelSpec;

/*
 * Reads the specification from a decompressed data model.
 *
 * Returns 0 on success, -1 on failure with a message in `error`. A model that legitimately has
 * no measures (or no relationships, or no calculated columns) is a success with zero of them:
 * plenty of real reports have none, and treating that as an error would reject valid input.
 */
int model_spec_read(const DataModel *model, ModelSpec *spec, char *error, size_t error_size);

/* Releases everything the specification owns. Safe on a zeroed specification. */
void model_spec_free(ModelSpec *spec);

#endif /* PBIX_METADATA_H */
