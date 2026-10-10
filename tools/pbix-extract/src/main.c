/*
 * pbix-extract: reads a Power BI report's semantic model and writes it as a SQLFlow YAML
 * specification.
 *
 * The purpose is to state what a report's model MEANS, in a form a SQL author (or a language
 * model asked to write SQL) can work from: the tables and columns that exist, what each measure
 * computes, which columns are themselves computed, how the tables relate, and where each table's
 * data comes from. None of that is in the report's visual layer, and none of it can be inferred
 * from the warehouse schema alone.
 *
 * Run offline against a .pbix already on disk; the output is reviewed like any other estate
 * source before it is used.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "buffer.h"
#include "datamodel.h"
#include "metadata.h"
#include "msource.h"
#include "reportlayout.h"
#include "yamlout.h"

#define ERROR_SIZE 1024

static void print_usage(FILE *stream, const char *program)
{
    fprintf(stream,
        "Usage: %s <report.pbix> [--name NAME] [--report-file PATH] [--out FILE]\n"
        "\n"
        "Reads a Power BI report's semantic model and its visual layer, and writes them as a\n"
        "SQLFlow YAML specification: flat `nodes:` and `edges:` lists (tables, columns,\n"
        "measures with their DAX, calculated columns, relationships, pages, visuals, and each\n"
        "visual's projected fields) forming an explicit graph, plus each table's Power Query\n"
        "source as a property on its table node.\n"
        "\n"
        "  --name NAME         subscriber name for the YAML entry (default: the file's base name)\n"
        "  --report-file PATH  label each page with this report file (default: the file name).\n"
        "                      Pass the path relative to the subscriber's declared directory when\n"
        "                      several reports are extracted under one subscriber, so two pages\n"
        "                      sharing a title stay distinguishable.\n"
        "  --out FILE          write to FILE instead of standard output\n"
        "  --help              show this message\n",
        program);
}

/* Returns the base name of a path, without its directory or .pbix extension. */
static char *report_name_from_path(const char *path)
{
    const char *slash = strrchr(path, '/');
    const char *base = slash != NULL ? slash + 1 : path;
    const char *dot = strrchr(base, '.');
    size_t length = dot != NULL ? (size_t)(dot - base) : strlen(base);
    char *name = (char *)malloc(length + 1);
    size_t i;

    if (name == NULL) {
        return NULL;
    }

    /* A subscriber name is an identity in the estate, so spaces become underscores to keep it
     * usable as a YAML key and as a lineage node name. */
    for (i = 0; i < length; i++) {
        name[i] = base[i] == ' ' ? '_' : base[i];
    }
    name[length] = '\0';
    return name;
}

/* Emits "key: value" at the given indent, choosing a block scalar for multi-line values. */
static int emit_field(Buffer *out, int indent, const char *key, const char *value)
{
    if (value == NULL) {
        return 0;
    }
    if (yaml_indent(out, indent) != 0
        || buffer_append_str(out, key) != 0
        || buffer_append_str(out, ": ") != 0
        || yaml_text(out, value, indent + 2) != 0) {
        return -1;
    }
    /* yaml_text ends a block scalar with its own newline; a quoted scalar needs one added. */
    if (out->size > 0 && out->data[out->size - 1] != '\n') {
        return buffer_append_str(out, "\n");
    }
    return 0;
}

/*
 * Node/edge graph emission.
 *
 * The spec is emitted as a flat `nodes:` list and a flat `edges:` list rather than a name-keyed
 * tree, so a consumer can load it directly into an in-memory graph with no cross-referencing step:
 * starting from one visual node and walking its `projects` edges to columns/measures, then their
 * `hasColumn`/`definedOn` edges back to tables, then the `relationship` edges between exactly those
 * tables, yields precisely that visual's relevant closure with no unrelated noise pulled in.
 *
 * Node ids follow the same derived-string-key convention the catalog already uses for
 * CatalogSubscriberReportPage/Visual (`SubscriberKey#reportFile#pageOrdinal#visualOrdinal`):
 * every id here is prefixed by the subscriber name and the report file, so ids stay globally
 * unique when many subscribers' (and many reports') graphs are merged into one. A `#` cannot
 * appear in a table/column/measure/page name here because Power BI does not allow it in an
 * entity name, so the separator is unambiguous.
 *
 * Every node carries a `kind` property so a generic consumer can filter by type without parsing
 * the id. Edges carry `from`, `to`, `kind`, and whatever extra properties that edge kind needs
 * (a `relationship` edge carries fromColumn/toColumn/cardinality/active; a `projects` edge
 * carries `role`). A `projects` edge deliberately does not repeat `isMeasure` as a boolean
 * property: which node kind it points at (`column` vs `measure`) already carries that
 * distinction structurally, so a consumer asks "what kind of node is this?" instead of reading a
 * second flag that could disagree with the first.
 */

/* Appends a quoted value for an id/property fragment, always as a plain (non-block) scalar: an id
 * is used as a mapping key's referenced value and must never split into a literal block even when
 * the underlying name happens to contain a newline (Power BI does not produce those in names, but
 * nothing enforces it), so this uses yaml_quoted rather than yaml_text. */
static int emit_id_field(Buffer *out, int indent, const char *key, const char *value)
{
    if (yaml_indent(out, indent) != 0
        || buffer_append_str(out, key) != 0
        || buffer_append_str(out, ": ") != 0
        || yaml_quoted(out, value) != 0
        || buffer_append_str(out, "\n") != 0) {
        return -1;
    }
    return 0;
}

/*
 * Concatenates 2 to 4 strings into a freshly allocated buffer. NULL-terminate the argument list is
 * not needed: every call site below passes a fixed count via one of the wrappers so there is no
 * ambiguity about how many operands follow. Returns NULL on allocation failure.
 *
 * A small hand-rolled joiner is used here, rather than asprintf, because the shipped tool's
 * Makefile deliberately builds under plain `-std=c11` (asprintf is a GNU/BSD extension gated
 * behind `_GNU_SOURCE`, which only the test harness defines), so every other allocation in this
 * file already goes through strdup/malloc rather than an extension.
 */
static char *concat4(const char *a, const char *b, const char *c, const char *d)
{
    size_t length = strlen(a) + strlen(b) + (c != NULL ? strlen(c) : 0) + (d != NULL ? strlen(d) : 0);
    char *result = (char *)malloc(length + 1);

    if (result == NULL) {
        return NULL;
    }
    result[0] = '\0';
    strcat(result, a);
    strcat(result, b);
    if (c != NULL) {
        strcat(result, c);
    }
    if (d != NULL) {
        strcat(result, d);
    }
    return result;
}

/* Builds "<subscriber>#<reportFile>#<rest>" into a freshly allocated string. Returns NULL on
 * allocation failure. */
static char *node_id(const char *subscriber, const char *report_file, const char *rest)
{
    char *prefix = concat4(subscriber, "#", report_file, "#");
    char *id;

    if (prefix == NULL) {
        return NULL;
    }
    id = concat4(prefix, rest, NULL, NULL);
    free(prefix);
    return id;
}

/* Builds "<table>.<name>" into a freshly allocated string, for a column/measure qualifier. */
static char *qualified(const char *table, const char *name)
{
    return concat4(table, ".", name, NULL);
}

/* Builds "<prefix>:<qualifier>" into a freshly allocated string, for a node-id rest fragment such
 * as "table:Sales" or "measure:Sales.Amount". */
static char *tagged(const char *prefix, const char *qualifier)
{
    return concat4(prefix, ":", qualifier, NULL);
}

/* Emits one node's "- id: ...\n  kind: ...\n" header into the nodes buffer; the caller appends any
 * extra properties. */
static int emit_node(Buffer *nodes, const char *id, const char *kind)
{
    if (yaml_indent(nodes, 4) != 0
        || buffer_append_str(nodes, "- id: ") != 0
        || yaml_quoted(nodes, id) != 0
        || buffer_append_str(nodes, "\n") != 0
        || emit_id_field(nodes, 6, "kind", kind) != 0) {
        return -1;
    }
    return 0;
}

/* Emits one edge's "- from / to / kind" header into the edges buffer at 4-space indent, with any
 * further properties the caller appends following at 6. */
static int emit_edge_header(Buffer *edges, const char *from, const char *to, const char *kind)
{
    if (yaml_indent(edges, 4) != 0
        || buffer_append_str(edges, "- from: ") != 0
        || yaml_quoted(edges, from) != 0
        || buffer_append_str(edges, "\n") != 0
        || emit_id_field(edges, 6, "to", to) != 0
        || emit_id_field(edges, 6, "kind", kind) != 0) {
        return -1;
    }
    return 0;
}

/*
 * Emits the report's visual layer as nodes and edges: a report node per file, a page node per
 * page (linked from the report by `hasPage`), a visual node per visual (linked from its page by
 * `hasVisual`), and a `projects` edge from each visual to the column or measure node it reads,
 * carrying the field's role. The report node also anchors `reportWarnings`, which stay a flat
 * list rather than graph-shaped facts (they are diagnostics about what was NOT extracted, not
 * something a consumer walks edges to reach).
 *
 * A `projects` edge targets a `col:<table>.<field>` node whenever the field is not a measure, or
 * a `measure:<table>.<field>` node when it is: this is how the isMeasure distinction survives the
 * move to a graph without a separate boolean on the edge (see the file-level comment above).
 */
static int emit_report(
    Buffer *nodes, Buffer *edges, const ReportLayout *layout, const char *name,
    const char *report_file)
{
    size_t page_index;
    char *report_node = node_id(name, report_file, "report");
    int status = -1;

    if (report_node == NULL) {
        return -1;
    }

    if (layout->page_count == 0) {
        free(report_node);
        return 0;
    }

    if (emit_node(nodes, report_node, "report") != 0) {
        goto done;
    }

    for (page_index = 0; page_index < layout->page_count; page_index++) {
        const ReportPage *page = &layout->pages[page_index];
        size_t visual_index;
        char page_rest[64];
        char *page_node;

        snprintf(page_rest, sizeof(page_rest), "page:%d", page->ordinal);
        page_node = node_id(name, report_file, page_rest);
        if (page_node == NULL) {
            goto done;
        }

        if (emit_node(nodes, page_node, "page") != 0
            || emit_id_field(nodes, 6, "displayName", page->display_name != NULL
                                        ? page->display_name : "") != 0
            || (page->name != NULL && emit_id_field(nodes, 6, "name", page->name) != 0)
            || emit_id_field(nodes, 6, "reportFile", report_file) != 0) {
            free(page_node);
            goto done;
        }
        {
            char number[32];

            snprintf(number, sizeof(number), "%d", page->ordinal);
            if (yaml_indent(nodes, 6) != 0
                || buffer_append_str(nodes, "ordinal: ") != 0
                || buffer_append_str(nodes, number) != 0
                || buffer_append_str(nodes, "\n") != 0) {
                free(page_node);
                goto done;
            }
        }

        if (emit_edge_header(edges, report_node, page_node, "hasPage") != 0) {
            free(page_node);
            goto done;
        }

        for (visual_index = 0; visual_index < page->visual_count; visual_index++) {
            const ReportVisual *visual = &page->visuals[visual_index];
            size_t field_index;
            char visual_rest[96];
            char *visual_node;

            snprintf(visual_rest, sizeof(visual_rest), "%s#visual:%d", page_rest, visual->ordinal);
            visual_node = node_id(name, report_file, visual_rest);
            if (visual_node == NULL) {
                free(page_node);
                goto done;
            }

            if (emit_node(nodes, visual_node, "visual") != 0
                || emit_id_field(nodes, 6, "visualType",
                       visual->visual_type != NULL ? visual->visual_type : "") != 0
                || (visual->title != NULL && emit_id_field(nodes, 6, "title", visual->title) != 0)
                || (visual->sql != NULL && emit_field(nodes, 6, "sql", visual->sql) != 0)) {
                free(visual_node);
                free(page_node);
                goto done;
            }
            {
                char number[32];

                snprintf(number, sizeof(number), "%d", visual->ordinal);
                if (yaml_indent(nodes, 6) != 0
                    || buffer_append_str(nodes, "ordinal: ") != 0
                    || buffer_append_str(nodes, number) != 0
                    || buffer_append_str(nodes, "\n") != 0) {
                    free(visual_node);
                    free(page_node);
                    goto done;
                }
            }

            if (emit_edge_header(edges, page_node, visual_node, "hasVisual") != 0) {
                free(visual_node);
                free(page_node);
                goto done;
            }

            for (field_index = 0; field_index < visual->field_count; field_index++) {
                const VisualField *field = &visual->fields[field_index];
                const char *table = field->table != NULL ? field->table : "";
                const char *column = field->column_or_measure != NULL
                    ? field->column_or_measure : "";
                char *qualifier = qualified(table, column);
                char *target_rest = NULL;
                char *target_node = NULL;

                if (qualifier == NULL) {
                    free(visual_node);
                    free(page_node);
                    goto done;
                }

                target_rest = tagged(field->is_measure ? "measure" : "col", qualifier);
                free(qualifier);
                if (target_rest == NULL) {
                    free(visual_node);
                    free(page_node);
                    goto done;
                }

                target_node = node_id(name, report_file, target_rest);
                free(target_rest);
                if (target_node == NULL) {
                    free(visual_node);
                    free(page_node);
                    goto done;
                }

                if (emit_edge_header(edges, visual_node, target_node, "projects") != 0
                    || emit_id_field(
                           edges, 6, "role", field->role != NULL ? field->role : "") != 0) {
                    free(target_node);
                    free(visual_node);
                    free(page_node);
                    goto done;
                }
                free(target_node);
            }

            free(visual_node);
        }

        free(page_node);
    }

    status = 0;

done:
    free(report_node);
    return status;
}

/*
 * Emits the semantic model as nodes and edges: one node per table, column, measure, and
 * calculated column; `hasColumn` edges from each table to its columns; `definedOn` edges from
 * each measure/calculated column to the table it is defined on; and `relationship` edges between
 * the tables a model relationship connects. A table's Power Query source is carried as a
 * `powerQuery` property directly on the table node rather than as a separate node, which keeps
 * the shape simpler without losing the expression text: nothing else ever needs to point AT a
 * source independently of its table.
 *
 * Where that expression names a warehouse object in a shape the resolver recognizes, the table node
 * additionally carries `sourceServer`/`sourceDatabase`/`sourceSchema`/`sourceName`: the physical
 * object the model table was loaded from, which is what lets a consumption edge from this report
 * land on the same node an ingestion flow writes instead of a name-only one. A table whose source
 * is not a recognized shape gets no such properties and one line appended to `warnings`, since a
 * silently missing mapping would make lineage look complete when it is not.
 */
/*
 * Writes one table's Power Query source as properties of the node just emitted, and its warning
 * when the expression names no warehouse object.
 *
 * This MUST be called immediately after that table's node line, because the emitted YAML is a
 * stream: a property line attaches to whichever node was written last, not to whichever node id a
 * caller happens to hold. Writing these in a later pass over the sources silently hung every
 * table's source on the final column node instead, which is invisible in the text unless a reader
 * checks WHICH node the block sits under, and left the resolution unusable downstream because a
 * consumer looks for these fields on a `table` node.
 *
 * Returns 0 when the table has no recorded source too, which is not an error: a table can exist
 * with no Power Query of its own.
 */
static int emit_table_source(
    Buffer *nodes, Buffer *warnings, const ModelSpec *spec, const char *table)
{
    const TableSource *source = NULL;
    MSourceResolution resolution;
    size_t i;

    for (i = 0; i < spec->source_count; i++) {
        if (spec->sources[i].table != NULL && strcmp(spec->sources[i].table, table) == 0) {
            source = &spec->sources[i];
            break;
        }
    }

    if (source == NULL) {
        return 0;
    }

    if (emit_id_field(nodes, 6, "powerQuery",
            source->expression != NULL ? source->expression : "") != 0) {
        return -1;
    }

    /* The physical object behind this model table, when the M expression names one in a shape the
     * resolver recognizes. The server is the M literal verbatim and is a CANDIDATE identity:
     * mapping it onto the estate's own connection reference is the consumer's decision, not this
     * tool's, so nothing is normalized here. */
    if (msource_resolve(source->expression, &resolution) != 0) {
        return -1;
    }

    if (resolution.resolved) {
        if (emit_id_field(nodes, 6, "sourceServer", resolution.server) != 0
            || emit_id_field(nodes, 6, "sourceDatabase", resolution.database) != 0
            || emit_id_field(nodes, 6, "sourceSchema", resolution.schema) != 0
            || emit_id_field(nodes, 6, "sourceName", resolution.item) != 0) {
            msource_free(&resolution);
            return -1;
        }
    } else if (source->expression != NULL && *source->expression != '\0') {
        char message[512];

        snprintf(message, sizeof(message),
            "model table '%s' is sourced via %s, which names no warehouse object; "
            "its lineage stays on the model entity name.",
            source->table,
            resolution.unresolved_shape != NULL
                ? resolution.unresolved_shape : "an unrecognized Power Query source");

        if (yaml_indent(warnings, 6) != 0
            || buffer_append_str(warnings, "- ") != 0
            || yaml_quoted(warnings, message) != 0
            || buffer_append_str(warnings, "\n") != 0) {
            msource_free(&resolution);
            return -1;
        }
    }

    msource_free(&resolution);
    return 0;
}

static int emit_model(
    Buffer *nodes, Buffer *edges, Buffer *warnings, const ModelSpec *spec, const char *name,
    const char *report_file)
{
    size_t i;

    /* Tables and their columns. A table node is emitted the first time a column names it, so a
     * table with no columns of its own only gets a node from the sources loop below. */
    for (i = 0; i < spec->column_count; i++) {
        const Column *column = &spec->columns[i];
        char *table_rest = NULL;
        char *table_node = NULL;
        char *column_rest = NULL;
        char *column_node = NULL;

        if (column->table == NULL || column->column == NULL) {
            continue;
        }

        table_rest = tagged("table", column->table);
        if (table_rest == NULL) {
            return -1;
        }
        table_node = node_id(name, report_file, table_rest);
        free(table_rest);
        if (table_node == NULL) {
            return -1;
        }

        /* A table node is emitted once, the first time one of its columns is seen; the model
         * reader lists a table's columns together, so consecutive columns sharing a table is the
         * common case, but this check is correctness rather than an optimization. */
        if (i == 0 || spec->columns[i - 1].table == NULL
            || strcmp(spec->columns[i - 1].table, column->table) != 0) {
            /* The source's properties belong to this node and are written while it is the last one
             * emitted; see emit_table_source. */
            if (emit_node(nodes, table_node, "table") != 0
                || emit_table_source(nodes, warnings, spec, column->table) != 0) {
                free(table_node);
                return -1;
            }
        }

        {
            char *qualifier = qualified(column->table, column->column);

            if (qualifier == NULL) {
                free(table_node);
                return -1;
            }
            column_rest = tagged("col", qualifier);
            free(qualifier);
        }
        if (column_rest == NULL) {
            free(table_node);
            return -1;
        }
        column_node = node_id(name, report_file, column_rest);
        free(column_rest);
        if (column_node == NULL) {
            free(table_node);
            return -1;
        }

        if (emit_node(nodes, column_node, "column") != 0
            || (column->data_type != NULL
                   && emit_id_field(nodes, 6, "dataType", column->data_type) != 0)
            || emit_edge_header(edges, table_node, column_node, "hasColumn") != 0) {
            free(column_node);
            free(table_node);
            return -1;
        }

        free(column_node);
        free(table_node);
    }

    /* A table with no columns of its own got no node above, so it is emitted here along with its
     * source. A table that DOES have columns already carries its source, written next to its node
     * where the properties actually attach, so it is skipped rather than emitted a second time. */
    for (i = 0; i < spec->source_count; i++) {
        const TableSource *source = &spec->sources[i];
        char *table_rest = NULL;
        char *table_node = NULL;
        int has_columns = 0;
        size_t j;

        if (source->table == NULL) {
            continue;
        }

        for (j = 0; j < spec->column_count; j++) {
            if (spec->columns[j].table != NULL && strcmp(spec->columns[j].table, source->table) == 0) {
                has_columns = 1;
                break;
            }
        }

        if (has_columns) {
            continue;
        }

        table_rest = tagged("table", source->table);
        if (table_rest == NULL) {
            return -1;
        }
        table_node = node_id(name, report_file, table_rest);
        free(table_rest);
        if (table_node == NULL) {
            return -1;
        }

        if (emit_node(nodes, table_node, "table") != 0
            || emit_table_source(nodes, warnings, spec, source->table) != 0) {
            free(table_node);
            return -1;
        }

        free(table_node);
    }
    /* Shared expressions: the model's not-loaded Power Query queries, one node each with its M,
     * so a consumer can follow a table's merge into them by name. */
    for (i = 0; i < spec->expression_count; i++) {
        const SharedExpression *expression = &spec->expressions[i];
        char *expression_rest;
        char *expression_node;

        if (expression->name == NULL) {
            continue;
        }

        expression_rest = tagged("expr", expression->name);
        if (expression_rest == NULL) {
            return -1;
        }
        expression_node = node_id(name, report_file, expression_rest);
        free(expression_rest);
        if (expression_node == NULL) {
            return -1;
        }

        if (emit_node(nodes, expression_node, "expression") != 0
            || emit_id_field(nodes, 6, "powerQuery",
                   expression->expression != NULL ? expression->expression : "") != 0) {
            free(expression_node);
            return -1;
        }

        free(expression_node);
    }

    /* Measures: a node per measure, carrying its DAX and description as properties, plus a
     * `definedOn` edge to the table it belongs to. */
    for (i = 0; i < spec->measure_count; i++) {
        const Measure *measure = &spec->measures[i];
        char *measure_node;
        char *table_node = NULL;

        if (measure->name == NULL) {
            continue;
        }

        {
            char *qualifier = qualified(
                measure->table != NULL ? measure->table : "", measure->name);
            char *measure_rest = NULL;

            if (qualifier == NULL) {
                return -1;
            }
            measure_rest = tagged("measure", qualifier);
            free(qualifier);
            if (measure_rest == NULL) {
                return -1;
            }
            measure_node = node_id(name, report_file, measure_rest);
            free(measure_rest);
        }
        if (measure_node == NULL) {
            return -1;
        }

        if (emit_node(nodes, measure_node, "measure") != 0
            || (measure->expression != NULL
                   && emit_field(nodes, 6, "dax", measure->expression) != 0)
            || (measure->description != NULL
                   && emit_field(nodes, 6, "description", measure->description) != 0)) {
            free(measure_node);
            return -1;
        }

        if (measure->table != NULL) {
            char *table_rest = tagged("table", measure->table);

            if (table_rest == NULL) {
                free(measure_node);
                return -1;
            }
            table_node = node_id(name, report_file, table_rest);
            free(table_rest);
            if (table_node == NULL
                || emit_edge_header(edges, measure_node, table_node, "definedOn") != 0) {
                free(table_node);
                free(measure_node);
                return -1;
            }
            free(table_node);
        }

        free(measure_node);
    }

    /* Calculated columns: same shape as measures, distinguished by node kind. */
    for (i = 0; i < spec->calculated_column_count; i++) {
        const CalculatedColumn *column = &spec->calculated_columns[i];
        char *calc_node;
        char *table_node = NULL;

        if (column->name == NULL) {
            continue;
        }

        {
            char *qualifier = qualified(
                column->table != NULL ? column->table : "", column->name);
            char *calc_rest = NULL;

            if (qualifier == NULL) {
                return -1;
            }
            calc_rest = tagged("calc", qualifier);
            free(qualifier);
            if (calc_rest == NULL) {
                return -1;
            }
            calc_node = node_id(name, report_file, calc_rest);
            free(calc_rest);
        }
        if (calc_node == NULL) {
            return -1;
        }

        if (emit_node(nodes, calc_node, "calculatedColumn") != 0
            || (column->expression != NULL
                   && emit_field(nodes, 6, "dax", column->expression) != 0)) {
            free(calc_node);
            return -1;
        }

        if (column->table != NULL) {
            char *table_rest = tagged("table", column->table);

            if (table_rest == NULL) {
                free(calc_node);
                return -1;
            }
            table_node = node_id(name, report_file, table_rest);
            free(table_rest);
            if (table_node == NULL
                || emit_edge_header(edges, calc_node, table_node, "definedOn") != 0) {
                free(table_node);
                free(calc_node);
                return -1;
            }
            free(table_node);
        }

        free(calc_node);
    }

    /* Relationships: a table-to-table edge carrying the join columns, cardinality, and whether
     * the relationship is active. */
    for (i = 0; i < spec->relationship_count; i++) {
        const Relationship *relationship = &spec->relationships[i];
        char *from_rest = NULL;
        char *from_node = NULL;
        char *to_rest = NULL;
        char *to_node = NULL;

        if (relationship->from_table == NULL || relationship->to_table == NULL) {
            continue;
        }

        from_rest = tagged("table", relationship->from_table);
        if (from_rest == NULL) {
            return -1;
        }
        from_node = node_id(name, report_file, from_rest);
        free(from_rest);

        to_rest = tagged("table", relationship->to_table);
        if (from_node == NULL || to_rest == NULL) {
            free(from_node);
            free(to_rest);
            return -1;
        }
        to_node = node_id(name, report_file, to_rest);
        free(to_rest);
        if (to_node == NULL) {
            free(from_node);
            return -1;
        }

        if (emit_edge_header(edges, from_node, to_node, "relationship") != 0
            || (relationship->from_column != NULL
                   && emit_id_field(edges, 6, "fromColumn", relationship->from_column) != 0)
            || (relationship->to_column != NULL
                   && emit_id_field(edges, 6, "toColumn", relationship->to_column) != 0)
            || (relationship->cardinality != NULL
                   && emit_id_field(edges, 6, "cardinality", relationship->cardinality) != 0)) {
            free(to_node);
            free(from_node);
            return -1;
        }
        /* An inactive relationship exists but is not applied unless a measure invokes it
         * (USERELATIONSHIP), so stating it is what keeps a generated join honest. */
        if (yaml_indent(edges, 6) != 0
            || buffer_append_str(edges, "active: ") != 0
            || buffer_append_str(edges, relationship->active ? "true\n" : "false\n") != 0) {
            free(to_node);
            free(from_node);
            return -1;
        }

        free(to_node);
        free(from_node);
    }

    return 0;
}

static int emit_spec(
    Buffer *out, const ModelSpec *spec, const ReportLayout *layout,
    const char *name, const char *source_file, const char *report_file)
{
    Buffer nodes;
    Buffer edges;
    /* Warnings raised while emitting the MODEL half (a table whose Power Query source names no
     * warehouse object). They are collected separately from the report layout's own warnings and
     * merged into the one reportWarnings block below, so a reader sees a single list of everything
     * this extraction could not carry, whichever half it came from. */
    Buffer model_warnings;
    int status = -1;
    size_t warning_index;

    buffer_init(&nodes);
    buffer_init(&edges);
    buffer_init(&model_warnings);

    if (buffer_append_str(out,
            "# Generated by tools/pbix-extract from a Power BI report.\n"
            "# The report as an explicit graph: flat `nodes:` and `edges:` lists rather than a\n"
            "# name-keyed tree, so a consumer loads it directly into an in-memory graph with no\n"
            "# cross-referencing step. Node ids are globally unique (subscriber#reportFile#...),\n"
            "# so graphs from many subscribers and reports can be merged without collisions.\n"
            "# Review before use.\n"
            "#\n") != 0) {
        goto done;
    }
    if (buffer_append_str(out, "# Source report: ") != 0
        || buffer_append_str(out, source_file) != 0
        || buffer_append_str(out, "\n\n") != 0) {
        goto done;
    }

    if (buffer_append_str(out, "subscribers:\n") != 0
        || yaml_indent(out, 2) != 0
        || buffer_append_str(out, name) != 0
        || buffer_append_str(out, ":\n") != 0
        || yaml_indent(out, 4) != 0
        || buffer_append_str(out, "type: PowerBI\n") != 0) {
        goto done;
    }

    if (emit_model(&nodes, &edges, &model_warnings, spec, name, report_file) != 0
        || emit_report(&nodes, &edges, layout, name, report_file) != 0) {
        goto done;
    }

    if (nodes.size > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "nodes:\n") != 0
            || buffer_append(out, nodes.data, nodes.size) != 0) {
            goto done;
        }
    }
    if (edges.size > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "edges:\n") != 0
            || buffer_append(out, edges.data, edges.size) != 0) {
            goto done;
        }
    }

    /* What was dropped and why. A skipped visual is a question this extraction does NOT carry, so
     * it is stated rather than left as a silent gap between the report and the spec. Kept as a
     * flat list rather than nodes/edges: these are diagnostics about extraction itself, not facts
     * about the model or the report a consumer would walk edges to reach. */
    if (layout->warning_count > 0 || model_warnings.size > 0) {
        if (yaml_indent(out, 4) != 0 || buffer_append_str(out, "reportWarnings:\n") != 0) {
            goto done;
        }
        for (warning_index = 0; warning_index < layout->warning_count; warning_index++) {
            if (yaml_indent(out, 6) != 0
                || buffer_append_str(out, "- ") != 0
                || yaml_quoted(out, layout->warnings[warning_index]) != 0
                || buffer_append_str(out, "\n") != 0) {
                goto done;
            }
        }
        if (model_warnings.size > 0
            && buffer_append(out, model_warnings.data, model_warnings.size) != 0) {
            goto done;
        }
    }

    status = 0;

done:
    buffer_free(&nodes);
    buffer_free(&edges);
    buffer_free(&model_warnings);
    return status;
}

int main(int argc, char **argv)
{
    const char *pbix_path = NULL;
    const char *out_path = NULL;
    const char *explicit_name = NULL;
    const char *report_file = NULL;
    char *report_name = NULL;
    char error[ERROR_SIZE];
    DataModel model;
    ModelSpec spec;
    ReportLayout layout;
    Buffer out;
    int model_read = 1;
    int status = EXIT_FAILURE;
    int i;

    memset(&model, 0, sizeof(model));
    memset(&spec, 0, sizeof(spec));
    memset(&layout, 0, sizeof(layout));
    buffer_init(&out);
    error[0] = '\0';

    for (i = 1; i < argc; i++) {
        if (strcmp(argv[i], "--help") == 0 || strcmp(argv[i], "-h") == 0) {
            print_usage(stdout, argv[0]);
            return EXIT_SUCCESS;
        }
        if (strcmp(argv[i], "--out") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "error: --out needs a file path\n");
                return EXIT_FAILURE;
            }
            out_path = argv[i];
        } else if (strcmp(argv[i], "--report-file") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "error: --report-file needs a value\n");
                goto done;
            }
            report_file = argv[i];
        } else if (strcmp(argv[i], "--name") == 0) {
            if (++i >= argc) {
                fprintf(stderr, "error: --name needs a value\n");
                return EXIT_FAILURE;
            }
            explicit_name = argv[i];
        } else if (argv[i][0] == '-') {
            fprintf(stderr, "error: unknown option '%s'\n", argv[i]);
            print_usage(stderr, argv[0]);
            return EXIT_FAILURE;
        } else if (pbix_path == NULL) {
            pbix_path = argv[i];
        } else {
            fprintf(stderr, "error: only one report can be read at a time\n");
            return EXIT_FAILURE;
        }
    }

    if (pbix_path == NULL) {
        print_usage(stderr, argv[0]);
        return EXIT_FAILURE;
    }

    /*
     * The two halves live in different parts of the file and are independent, so either can fail
     * without costing the other: the failure is reported and that section omitted, rather than
     * losing the half that did work.
     *
     * The model half is absent for a whole legitimate class of report. One connected live to a
     * published dataset keeps its model on the server, so the file holds no `DataModel` at all
     * while still carrying a complete visual layer, which is the half that records the questions
     * people actually asked. Treating that as fatal would refuse exactly those reports.
     */
    if (data_model_open(pbix_path, &model, error, sizeof(error)) != 0
        || model_spec_read(&model, &spec, error, sizeof(error)) != 0) {
        fprintf(stderr, "warning: the report's model could not be read: %s\n", error);
        model_spec_free(&spec);
        memset(&spec, 0, sizeof(spec));
        model_read = 0;
    }

    if (report_layout_read(pbix_path, &layout, error, sizeof(error)) != 0) {
        fprintf(stderr, "warning: the report's visuals could not be read: %s\n", error);
        memset(&layout, 0, sizeof(layout));
    }

    /* Neither half readable means the file yielded nothing at all, which is a real failure. */
    if (!model_read && layout.page_count == 0) {
        fprintf(stderr, "error: '%s' yielded neither a model nor a report layout\n", pbix_path);
        goto done;
    }

    if (explicit_name != NULL) {
        report_name = strdup(explicit_name);
    } else {
        report_name = report_name_from_path(pbix_path);
    }
    if (report_name == NULL) {
        fprintf(stderr, "error: out of memory\n");
        goto done;
    }

    if (report_file == NULL) {
        const char *slash = strrchr(pbix_path, '/');

        report_file = slash != NULL ? slash + 1 : pbix_path;
    }

    if (emit_spec(&out, &spec, &layout, report_name, pbix_path, report_file) != 0) {
        fprintf(stderr, "error: out of memory writing the specification\n");
        goto done;
    }

    if (out_path != NULL) {
        FILE *file = fopen(out_path, "wb");

        if (file == NULL) {
            fprintf(stderr, "error: '%s' could not be opened for writing\n", out_path);
            goto done;
        }
        if (fwrite(out.data, 1, out.size, file) != out.size) {
            fprintf(stderr, "error: '%s' could not be written\n", out_path);
            fclose(file);
            goto done;
        }
        if (fclose(file) != 0) {
            fprintf(stderr, "error: '%s' could not be closed cleanly\n", out_path);
            goto done;
        }
        {
            size_t visual_total = 0;
            size_t page_index;

            for (page_index = 0; page_index < layout.page_count; page_index++) {
                visual_total += layout.pages[page_index].visual_count;
            }

            fprintf(stderr,
                "wrote %s: %zu tables' columns, %zu measures, %zu calculated columns, "
                "%zu relationships, %zu table sources, %zu shared expressions, %zu pages, "
                "%zu visuals\n",
                out_path, spec.column_count, spec.measure_count, spec.calculated_column_count,
                spec.relationship_count, spec.source_count, spec.expression_count, layout.page_count,
                visual_total);
        }
    } else {
        if (fwrite(out.data, 1, out.size, stdout) != out.size) {
            fprintf(stderr, "error: the specification could not be written to standard output\n");
            goto done;
        }
    }

    status = EXIT_SUCCESS;

done:
    free(report_name);
    buffer_free(&out);
    report_layout_free(&layout);
    model_spec_free(&spec);
    data_model_free(&model);
    return status;
}
