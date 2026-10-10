/*
 * Renders a visual's query, and the filters that apply to it, as one T-SQL SELECT.
 *
 * There is ONE renderer rather than two because a Power BI filter is written in the same expression
 * language as the query it constrains: the same Column, SourceRef, Literal, In and Not nodes appear
 * in both. Folding the filters into the statement's WHERE clause is what keeps a filtered visual an
 * honest record of its question: a chart of sales restricted to one business type asks a narrower
 * question than the same chart unrestricted, and storing the restriction anywhere other than the
 * query would leave something that still has to be interpreted later.
 *
 * The output exists to be READ, by the estate's existing T-SQL lineage parser and by a person: it
 * names the entities the visual reads so the objects resolve to the same catalog nodes the loading
 * flows write. It is not meant to be executed against the warehouse, and it deliberately carries no
 * database or schema qualification, since a visual names model entities and the resolution to
 * physical objects (including through old-production compatibility views) is the catalog's job, not
 * this renderer's.
 */

#ifndef PBIX_SQLRENDER_H
#define PBIX_SQLRENDER_H

#include <stddef.h>

#include "reportlayout.h"

/*
 * Renders one visual as a SELECT whose FROM names the entities it reads, whose SELECT list carries
 * its projected fields and measures, and whose WHERE carries every filter that applies to it (the
 * visual's own, plus the page-level filters passed in).
 *
 * Returns 0 on success, with the rendered statement allocated into `*sql_out` for the caller to
 * free. Returns 1 when the visual is REFUSED, writing the reason into `refusal` and leaving
 * `*sql_out` NULL: an expression with no SQL rendering must not be dropped quietly, since a query
 * missing part of its filter is broader than the question the visual asks. Returns -1 on allocation
 * failure.
 */
int sql_render_visual(
    const ReportVisual *visual,
    const ReportFilter *page_filters, size_t page_filter_count,
    char **sql_out, char *refusal, size_t refusal_size);

#endif /* PBIX_SQLRENDER_H */
