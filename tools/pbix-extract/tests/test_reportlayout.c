/*
 * Tests the visual layer: what a report says people already asked, and the one failure that must
 * never pass silently.
 *
 * A chart titled "Sales Amount by Category" is a business question whose shape is already settled,
 * and the ROLE each field plays (what the chart is broken down BY versus what it plots) is the part
 * of that shape a flat column list from parsed SQL cannot express. These tests pin that extraction
 * down, and pin down the thing that would make an extracted question WRONG: dropping a filter,
 * which would leave a query broader than the question actually on screen.
 *
 * The fixtures are synthetic .pbix files built here rather than checked-in binaries, and cover BOTH
 * shapes Power BI Desktop writes. The older one is a zip whose `Report/Layout` is UTF-16LE JSON
 * with a visual's `config` and `filters` held as JSON STRINGS inside that JSON; the newer one
 * spreads the report over `Report/definition/...`, one plain UTF-8 document per page and per
 * visual, with field expressions inline rather than behind a queryRef. Both are what the reader has
 * to cope with, so both are exercised here. The expected values were confirmed against a genuine
 * AdventureWorks report saved in each format.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <zip.h>

#include "reportlayout.h"
#include "test_msource.h"

static int failures;
static int checks;

static void check(int condition, const char *what)
{
    checks++;
    if (!condition) {
        failures++;
        fprintf(stderr, "FAIL: %s\n", what);
    }
}

static void check_str(const char *actual, const char *expected, const char *what)
{
    checks++;
    if (actual == NULL || strcmp(actual, expected) != 0) {
        failures++;
        fprintf(stderr, "FAIL: %s\n  expected: %s\n  actual:   %s\n",
            what, expected, actual != NULL ? actual : "(null)");
    }
}

/* True when `haystack` contains `needle`. */
static int contains(const char *haystack, const char *needle)
{
    return haystack != NULL && strstr(haystack, needle) != NULL;
}

/* True when any recorded warning contains both fragments. */
static int warned(const ReportLayout *layout, const char *first, const char *second)
{
    size_t i;

    for (i = 0; i < layout->warning_count; i++) {
        if (contains(layout->warnings[i], first) && contains(layout->warnings[i], second)) {
            return 1;
        }
    }
    return 0;
}

/*
 * Encodes UTF-8 (ASCII only, which every fixture here is) as the UTF-16LE Power BI writes.
 * Reading the part as UTF-8 would yield interleaved NULs, so the encoding is part of the contract
 * the reader honors and part of what these fixtures exercise.
 */
static unsigned char *to_utf16le(const char *text, size_t *size_out)
{
    size_t length = strlen(text);
    unsigned char *bytes = (unsigned char *)malloc(length * 2);
    size_t i;

    if (bytes == NULL) {
        return NULL;
    }
    for (i = 0; i < length; i++) {
        bytes[i * 2] = (unsigned char)text[i];
        bytes[i * 2 + 1] = 0;
    }
    *size_out = length * 2;
    return bytes;
}

/* Writes a .pbix: a zip whose Report/Layout entry is UTF-16LE JSON. Returns 0 on success. */
static int write_pbix(const char *path, const char *layout_json)
{
    zip_t *archive;
    zip_source_t *source;
    unsigned char *utf16;
    size_t size = 0;
    int error = 0;

    remove(path);
    archive = zip_open(path, ZIP_CREATE | ZIP_TRUNCATE, &error);
    if (archive == NULL) {
        return -1;
    }

    utf16 = to_utf16le(layout_json, &size);
    if (utf16 == NULL) {
        zip_discard(archive);
        return -1;
    }

    /* The source takes ownership of the buffer (freep = 1), so it is not freed here. */
    source = zip_source_buffer(archive, utf16, size, 1);
    if (source == NULL || zip_file_add(archive, "Report/Layout", source, ZIP_FL_ENC_UTF_8) < 0) {
        if (source != NULL) {
            zip_source_free(source);
        } else {
            free(utf16);
        }
        zip_discard(archive);
        return -1;
    }

    return zip_close(archive) == 0 ? 0 : -1;
}

/* An area chart: a hierarchy level on the category axis, an aggregated column and a measure on Y. */
static const char AREA_CHART_CONFIG[] =
    "{\"singleVisual\":{\"visualType\":\"areaChart\","
    "\"projections\":{"
    "\"Category\":[{\"queryRef\":\"Date.Fiscal.Month\"}],"
    "\"Y\":[{\"queryRef\":\"Sum(Sales.Sales Amount)\"},{\"queryRef\":\"Sales.Sales Amount by Due Date\"}]},"
    "\"prototypeQuery\":{\"Version\":2,"
    "\"From\":[{\"Name\":\"d\",\"Entity\":\"Date\",\"Type\":0},"
    "{\"Name\":\"s\",\"Entity\":\"Sales\",\"Type\":0}],"
    "\"Select\":["
    "{\"Aggregation\":{\"Expression\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},"
    "\"Property\":\"Sales Amount\"}},\"Function\":0},\"Name\":\"Sum(Sales.Sales Amount)\"},"
    "{\"Measure\":{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},"
    "\"Property\":\"Sales Amount by Due Date\"},\"Name\":\"Sales.Sales Amount by Due Date\"},"
    "{\"HierarchyLevel\":{\"Expression\":{\"Hierarchy\":{\"Expression\":{\"SourceRef\":{\"Source\":\"d\"}},"
    "\"Hierarchy\":\"Fiscal\"}},\"Level\":\"Month\"},\"Name\":\"Date.Fiscal.Month\"}],"
    "\"OrderBy\":[{\"Direction\":1,\"Expression\":{\"HierarchyLevel\":{\"Expression\":{\"Hierarchy\":"
    "{\"Expression\":{\"SourceRef\":{\"Source\":\"d\"}},\"Hierarchy\":\"Fiscal\"}},\"Level\":\"Month\"}}}]},"
    "\"vcObjects\":{\"title\":[{\"properties\":{\"text\":{\"expr\":{\"Literal\":"
    "{\"Value\":\"'Sales Amount by Order Date / Due Date'\"}}}}}]}}}";

/* A pivot table sorted descending by its measure: two row fields and one value field. */
static const char PIVOT_TABLE_CONFIG[] =
    "{\"singleVisual\":{\"visualType\":\"pivotTable\","
    "\"projections\":{"
    "\"Rows\":[{\"queryRef\":\"Product.Category\"},{\"queryRef\":\"Reseller.Business Type\"}],"
    "\"Values\":[{\"queryRef\":\"Sum(Sales.Sales Amount)\"}]},"
    "\"prototypeQuery\":{\"Version\":2,"
    "\"From\":[{\"Name\":\"p\",\"Entity\":\"Product\",\"Type\":0},"
    "{\"Name\":\"r\",\"Entity\":\"Reseller\",\"Type\":0},"
    "{\"Name\":\"s\",\"Entity\":\"Sales\",\"Type\":0}],"
    "\"Select\":["
    "{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"p\"}},\"Property\":\"Category\"},"
    "\"Name\":\"Product.Category\"},"
    "{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"r\"}},\"Property\":\"Business Type\"},"
    "\"Name\":\"Reseller.Business Type\"},"
    "{\"Aggregation\":{\"Expression\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},"
    "\"Property\":\"Sales Amount\"}},\"Function\":0},\"Name\":\"Sum(Sales.Sales Amount)\"}],"
    "\"OrderBy\":[{\"Direction\":2,\"Expression\":{\"Aggregation\":{\"Expression\":{\"Column\":"
    "{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Sales Amount\"}},"
    "\"Function\":0}}}]}}}";

/*
 * The pivot table's filter, exactly as Power BI stores one: a Not over an In, written in the SAME
 * expression language as the query it constrains. This is the shape that makes one renderer enough.
 */
static const char PIVOT_TABLE_FILTERS[] =
    "[{\"name\":\"Filterf32699ca5c7851734a77\","
    "\"filter\":{\"Version\":2,"
    "\"From\":[{\"Name\":\"r\",\"Entity\":\"Reseller\",\"Type\":0}],"
    "\"Where\":[{\"Condition\":{\"Not\":{\"Expression\":{\"In\":{"
    "\"Expressions\":[{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"r\"}},"
    "\"Property\":\"Business Type\"}}],"
    "\"Values\":[[{\"Literal\":{\"Value\":\"'[Not Applicable]'\"}}]]}}}}}]},"
    "\"type\":\"Categorical\"}]";

/* A textbox and a shape: they project nothing, so they are decoration rather than questions. */
static const char TEXTBOX_CONFIG[] =
    "{\"singleVisual\":{\"visualType\":\"textbox\",\"projections\":{},\"objects\":{}}}";
static const char BASIC_SHAPE_CONFIG[] =
    "{\"singleVisual\":{\"visualType\":\"basicShape\",\"projections\":{}}}";

/* Serializes `document` as a JSON STRING value, which is how Power BI nests config and filters. */
static char *as_json_string(const char *document)
{
    size_t length = strlen(document);
    char *encoded = (char *)malloc(length * 2 + 3);
    size_t out = 0;
    size_t i;

    if (encoded == NULL) {
        return NULL;
    }
    encoded[out++] = '"';
    for (i = 0; i < length; i++) {
        if (document[i] == '"' || document[i] == '\\') {
            encoded[out++] = '\\';
        }
        encoded[out++] = document[i];
    }
    encoded[out++] = '"';
    encoded[out] = '\0';
    return encoded;
}

/* Builds one visual container holding a config and its filters. */
static char *container(const char *config, const char *filters)
{
    char *config_json = as_json_string(config);
    char *filters_json = as_json_string(filters);
    char *text = NULL;
    int written;

    if (config_json == NULL || filters_json == NULL) {
        free(config_json);
        free(filters_json);
        return NULL;
    }

    written = asprintf(&text,
        "{\"x\":0,\"y\":0,\"z\":0,\"width\":100,\"height\":100,\"config\":%s,\"filters\":%s}",
        config_json, filters_json);
    free(config_json);
    free(filters_json);
    return written < 0 ? NULL : text;
}

/* Builds a one-page layout document from pre-serialized containers. */
static char *layout_with(const char *containers, const char *page_filters)
{
    char *filters_json = as_json_string(page_filters);
    char *text = NULL;
    int written;

    if (filters_json == NULL) {
        return NULL;
    }
    written = asprintf(&text,
        "{\"id\":0,\"sections\":[{\"id\":1,\"name\":\"s1\",\"displayName\":\"Page 1\","
        "\"ordinal\":1,\"filters\":%s,\"visualContainers\":[%s]}]}",
        filters_json, containers);
    free(filters_json);
    return written < 0 ? NULL : text;
}

/* Reads a layout document through a real .pbix, so the zip and UTF-16 paths are exercised too. */
static int read_layout(const char *layout_json, ReportLayout *layout)
{
    const char *path = "build/test-fixture.pbix";
    char error[512];

    error[0] = '\0';
    if (write_pbix(path, layout_json) != 0) {
        fprintf(stderr, "FAIL: could not write the test fixture\n");
        failures++;
        return -1;
    }
    if (report_layout_read(path, layout, error, sizeof(error)) != 0) {
        fprintf(stderr, "FAIL: reading the fixture failed: %s\n", error);
        failures++;
        return -1;
    }
    return 0;
}

/*
 * Four containers in, two visuals out, with each field's role, table and name resolved: a hierarchy
 * level to its LEVEL, an aggregation to the field it aggregates, a measure flagged as one.
 */
static void test_extracts_pages_visuals_and_field_roles(void)
{
    char *chart = container(AREA_CHART_CONFIG, "[]");
    char *pivot = container(PIVOT_TABLE_CONFIG, PIVOT_TABLE_FILTERS);
    char *textbox = container(TEXTBOX_CONFIG, "[]");
    char *shape = container(BASIC_SHAPE_CONFIG, "[]");
    char *containers = NULL;
    char *document = NULL;
    ReportLayout layout;

    if (chart == NULL || pivot == NULL || textbox == NULL || shape == NULL
        || asprintf(&containers, "%s,%s,%s,%s", chart, pivot, textbox, shape) < 0) {
        fprintf(stderr, "FAIL: out of memory building the fixture\n");
        failures++;
        goto done;
    }

    document = layout_with(containers, "[]");
    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    check(layout.page_count == 1, "one page is read");
    if (layout.page_count == 1) {
        const ReportPage *page = &layout.pages[0];

        check_str(page->display_name, "Page 1", "the page keeps its display name");
        check_str(page->name, "s1", "the page keeps its internal name");
        check(page->ordinal == 1, "the page keeps its position");

        /* The textbox and the shape project no field, so they ask no question and are deliberately
         * not recorded as ones. */
        check(page->visual_count == 2, "decoration is dropped, leaving two visuals");

        if (page->visual_count == 2) {
            const ReportVisual *area = &page->visuals[0];

            check_str(area->visual_type, "areaChart", "the chart keeps its type");
            /* The title is stored as a single-quoted literal and must come back unquoted. */
            check_str(area->title, "Sales Amount by Order Date / Due Date",
                "the authored title is unquoted");
            check(area->ordinal == 1, "the visual keeps its position");
            check(area->field_count == 3, "the chart resolves three fields");

            if (area->field_count == 3) {
                check_str(area->fields[0].role, "Category", "the hierarchy level is the axis");
                check_str(area->fields[0].table, "Date", "the axis resolves to its table");
                check_str(area->fields[0].column_or_measure, "Month",
                    "a hierarchy level resolves to its LEVEL");
                check(!area->fields[0].is_measure, "the axis is not a measure");

                check_str(area->fields[1].role, "Y", "the aggregated column is plotted");
                check_str(area->fields[1].column_or_measure, "Sales Amount",
                    "an aggregation is unwrapped to the field it aggregates");
                check(!area->fields[1].is_measure, "an aggregated column is not a measure");

                check_str(area->fields[2].column_or_measure, "Sales Amount by Due Date",
                    "the measure keeps its name");
                check(area->fields[2].is_measure == 1, "a measure is flagged as one");
            }
        }
    }
    check(layout.warning_count == 0, "a clean report warns about nothing");
    report_layout_free(&layout);

done:
    free(chart);
    free(pivot);
    free(textbox);
    free(shape);
    free(containers);
    free(document);
}

/* The whole question in one statement: fields, tables, the filter that narrows it, and the sort. */
static void test_renders_the_visuals_query_with_its_filter(void)
{
    char *pivot = container(PIVOT_TABLE_CONFIG, PIVOT_TABLE_FILTERS);
    char *document = pivot != NULL ? layout_with(pivot, "[]") : NULL;
    ReportLayout layout;

    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        check_str(layout.pages[0].visuals[0].sql,
            "SELECT [p].[Category] AS [Product.Category], "
            "[r].[Business Type] AS [Reseller.Business Type], "
            "SUM([s].[Sales Amount]) AS [Sum(Sales.Sales Amount)] "
            "FROM [Product] AS [p], [Reseller] AS [r], [Sales] AS [s] "
            "WHERE NOT ([r].[Business Type] IN ('[Not Applicable]')) "
            "ORDER BY SUM([s].[Sales Amount]) DESC;",
            "the pivot table renders with its filter folded into WHERE");
    } else {
        check(0, "the pivot table produced one visual");
    }
    report_layout_free(&layout);

done:
    free(pivot);
    free(document);
}

/* Aggregations, measures and hierarchy levels each render as the reference a reader looks for. */
static void test_renders_aggregations_measures_and_hierarchy_levels(void)
{
    char *chart = container(AREA_CHART_CONFIG, "[]");
    char *document = chart != NULL ? layout_with(chart, "[]") : NULL;
    ReportLayout layout;

    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        check_str(layout.pages[0].visuals[0].sql,
            "SELECT SUM([s].[Sales Amount]) AS [Sum(Sales.Sales Amount)], "
            "[s].[Sales Amount by Due Date] AS [Sales.Sales Amount by Due Date], "
            "[d].[Month] AS [Date.Fiscal.Month] "
            "FROM [Date] AS [d], [Sales] AS [s] "
            "ORDER BY [d].[Month] ASC;",
            "the area chart renders its aggregation, measure and hierarchy level");
    } else {
        check(0, "the area chart produced one visual");
    }
    report_layout_free(&layout);

done:
    free(chart);
    free(document);
}

/*
 * A page filter applies to every visual on the page, so it narrows each visual's question just as
 * the visual's own filter does. Leaving it out would record a question broader than the one shown.
 */
static void test_folds_page_level_filters_into_every_visual(void)
{
    char *chart = container(AREA_CHART_CONFIG, "[]");
    char *document = chart != NULL ? layout_with(chart, PIVOT_TABLE_FILTERS) : NULL;
    ReportLayout layout;

    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        check(contains(layout.pages[0].visuals[0].sql,
                  "WHERE NOT ([r].[Business Type] IN ('[Not Applicable]'))"),
            "a page-level filter narrows a visual that has no filter of its own");
    } else {
        check(0, "the filtered page produced one visual");
    }
    report_layout_free(&layout);

done:
    free(chart);
    free(document);
}

/*
 * The one failure that must never pass silently: an untranslatable filter would leave a query
 * BROADER than the visual's real question, which is exactly how a confirmed example ends up wrong.
 * Refusing the whole visual, loudly, is the honest outcome.
 */
static void test_refuses_a_visual_whose_filter_it_cannot_translate(void)
{
    static const char UNSUPPORTED[] =
        "[{\"name\":\"Filter1\",\"filter\":{\"Version\":2,"
        "\"From\":[{\"Name\":\"r\",\"Entity\":\"Reseller\",\"Type\":0}],"
        "\"Where\":[{\"Condition\":{\"SomeFutureOperator\":{\"Left\":{},\"Right\":{}}}}]}}]";
    char *pivot = container(PIVOT_TABLE_CONFIG, UNSUPPORTED);
    char *document = pivot != NULL ? layout_with(pivot, "[]") : NULL;
    ReportLayout layout;

    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    check(layout.page_count == 1 && layout.pages[0].visual_count == 0,
        "a visual with an untranslatable filter is dropped rather than narrowed");
    check(warned(&layout, "unsupported expression", "widen the question"),
        "the refusal says what was skipped and why");
    report_layout_free(&layout);

done:
    free(pivot);
    free(document);
}

/*
 * A projection naming a queryRef the query never selects cannot be resolved to a column, so it is
 * skipped with a warning rather than recorded as a field with no identity.
 */
static void test_reports_a_projection_the_query_does_not_select(void)
{
    static const char DANGLING[] =
        "{\"singleVisual\":{\"visualType\":\"barChart\","
        "\"projections\":{\"Y\":[{\"queryRef\":\"Sum(Sales.Missing)\"},"
        "{\"queryRef\":\"Product.Category\"}]},"
        "\"prototypeQuery\":{\"Version\":2,"
        "\"From\":[{\"Name\":\"p\",\"Entity\":\"Product\",\"Type\":0}],"
        "\"Select\":[{\"Column\":{\"Expression\":{\"SourceRef\":{\"Source\":\"p\"}},"
        "\"Property\":\"Category\"},\"Name\":\"Product.Category\"}]}}}";
    char *visual = container(DANGLING, "[]");
    char *document = visual != NULL ? layout_with(visual, "[]") : NULL;
    ReportLayout layout;

    if (document == NULL || read_layout(document, &layout) != 0) {
        goto done;
    }

    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        check(layout.pages[0].visuals[0].field_count == 1,
            "the resolvable field is kept when its sibling dangles");
        check_str(layout.pages[0].visuals[0].fields[0].table, "Product",
            "the kept field resolves to its table");
    } else {
        check(0, "the visual survived with its one resolvable field");
    }
    check(warned(&layout, "Sum(Sales.Missing)", "which its query does not select"),
        "the dangling projection is named in a warning");
    report_layout_free(&layout);

done:
    free(visual);
    free(document);
}

/* A file holding a model but no built report is not an error: there are simply no questions yet. */
static void test_a_report_with_no_sections_is_not_an_error(void)
{
    ReportLayout layout;

    if (read_layout("{\"id\":0}", &layout) != 0) {
        return;
    }
    check(layout.page_count == 0, "a layout with no sections yields no pages");
    report_layout_free(&layout);
}

/* ---- The split report format (Report/definition/...) ------------------------------------- */

/* One member of a split-format fixture: a path in the zip and the UTF-8 JSON stored at it. */
typedef struct {
    const char *path;
    const char *json;
} SplitMember;

/*
 * Writes a .pbix in the split format: one member per document, plain UTF-8 rather than the older
 * format's UTF-16LE, and no stringified JSON anywhere. That difference is the reader contract
 * these fixtures exist to exercise.
 */
static int write_pbix_split(const char *path, const SplitMember *members, size_t count)
{
    zip_t *archive;
    int error = 0;
    size_t i;

    remove(path);
    archive = zip_open(path, ZIP_CREATE | ZIP_TRUNCATE, &error);
    if (archive == NULL) {
        return -1;
    }

    for (i = 0; i < count; i++) {
        /* The buffer is NOT copied by libzip, so each fixture's JSON must outlive zip_close; every
         * caller passes string literals, which do. */
        zip_source_t *source = zip_source_buffer(
            archive, members[i].json, strlen(members[i].json), 0);

        if (source == NULL
            || zip_file_add(archive, members[i].path, source, ZIP_FL_ENC_UTF_8) < 0) {
            if (source != NULL) {
                zip_source_free(source);
            }
            zip_discard(archive);
            return -1;
        }
    }

    return zip_close(archive) == 0 ? 0 : -1;
}

static int read_split_layout(const SplitMember *members, size_t count, ReportLayout *layout)
{
    const char *path = "build/test-fixture-split.pbix";
    char error[512];

    error[0] = '\0';
    if (write_pbix_split(path, members, count) != 0) {
        fprintf(stderr, "FAIL: could not write the split test fixture\n");
        failures++;
        return -1;
    }
    if (report_layout_read(path, layout, error, sizeof(error)) != 0) {
        fprintf(stderr, "FAIL: reading the split fixture failed: %s\n", error);
        failures++;
        return -1;
    }
    return 0;
}

/*
 * The split format's own shape, read end to end: a field expression held INLINE under its role
 * rather than behind a queryRef, a FROM reconstructed from the entities those fields name, a
 * spelled-out sort direction, and a title under `visualContainerObjects`.
 */
static void test_reads_the_split_report_format(void)
{
    static const SplitMember MEMBERS[] = {
        { "Report/definition/pages/pages.json",
          "{\"pageOrder\":[\"p1\"],\"activePageName\":\"p1\"}" },
        { "Report/definition/pages/p1/page.json",
          "{\"name\":\"p1\",\"displayName\":\"Page 1\"}" },
        { "Report/definition/pages/p1/visuals/v1/visual.json",
          "{\"name\":\"v1\",\"visual\":{\"visualType\":\"pivotTable\",\"query\":{\"queryState\":{"
          "\"Rows\":{\"projections\":[{\"field\":{\"Column\":{\"Expression\":{\"SourceRef\":"
          "{\"Entity\":\"Product\"}},\"Property\":\"Category\"}},"
          "\"queryRef\":\"Product.Category\"}]},"
          "\"Values\":{\"projections\":[{\"field\":{\"Aggregation\":{\"Expression\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Sales Amount\"}},"
          "\"Function\":0}},\"queryRef\":\"Sum(Sales.Sales Amount)\"}]}},"
          "\"sortDefinition\":{\"sort\":[{\"field\":{\"Aggregation\":{\"Expression\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Sales Amount\"}},"
          "\"Function\":0}},\"direction\":\"Descending\"}]}},"
          "\"visualContainerObjects\":{\"title\":[{\"properties\":{\"text\":{\"expr\":{\"Literal\":"
          "{\"Value\":\"'Sales Amount by Category'\"}}}}}]}}}" }
    };
    ReportLayout layout;

    if (read_split_layout(MEMBERS, sizeof(MEMBERS) / sizeof(MEMBERS[0]), &layout) != 0) {
        return;
    }

    check(layout.page_count == 1, "the split format yields its one page");
    if (layout.page_count == 1) {
        const ReportPage *page = &layout.pages[0];

        check_str(page->display_name, "Page 1", "the page's display name is read");
        check(page->visual_count == 1, "the page's one visual is read");
        if (page->visual_count == 1) {
            const ReportVisual *visual = &page->visuals[0];

            check_str(visual->visual_type, "pivotTable", "the visual's type is read");
            check_str(visual->title, "Sales Amount by Category",
                "the title is read from visualContainerObjects");
            check(visual->field_count == 2, "both inline projections become fields");
            if (visual->field_count == 2) {
                check_str(visual->fields[0].role, "Rows", "the first field keeps its role");
                check_str(visual->fields[0].table, "Product",
                    "an entity-qualified reference resolves without an alias binding");
                check_str(visual->fields[0].column_or_measure, "Category",
                    "the first field's column is resolved");
                check_str(visual->fields[1].column_or_measure, "Sales Amount",
                    "an aggregation resolves to the field it aggregates");
            }
            /* The FROM is reconstructed: this format states no query-level FROM of its own. */
            check_str(visual->sql,
                "SELECT [Product].[Category] AS [Product.Category], "
                "SUM([Sales].[Sales Amount]) AS [Sum(Sales.Sales Amount)] "
                "FROM [Product] AS [Product], [Sales] AS [Sales] "
                "ORDER BY SUM([Sales].[Sales Amount]) DESC;",
                "the question renders as one SELECT over the entities its fields name");
        }
    }
    report_layout_free(&layout);
}

/*
 * A split-format filter reaches the WHERE clause. This is the same failure the older format's test
 * guards: a filter silently dropped leaves a query BROADER than the question on screen.
 */
static void test_folds_split_format_filters_into_the_query(void)
{
    static const SplitMember MEMBERS[] = {
        { "Report/definition/pages/pages.json", "{\"pageOrder\":[\"p1\"]}" },
        { "Report/definition/pages/p1/page.json",
          "{\"name\":\"p1\",\"displayName\":\"Page 1\",\"filterConfig\":{\"filters\":[{"
          "\"name\":\"pageFilter\",\"filter\":{\"From\":[{\"Name\":\"d\",\"Entity\":\"Date\"}],"
          "\"Where\":[{\"Condition\":{\"Comparison\":{\"ComparisonKind\":2,\"Left\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Source\":\"d\"}},\"Property\":\"Fiscal Year\"}},"
          "\"Right\":{\"Literal\":{\"Value\":\"2020L\"}}}}}]}}]}}" },
        { "Report/definition/pages/p1/visuals/v1/visual.json",
          "{\"name\":\"v1\",\"visual\":{\"visualType\":\"barChart\",\"query\":{\"queryState\":{"
          "\"Y\":{\"projections\":[{\"field\":{\"Column\":{\"Expression\":{\"SourceRef\":"
          "{\"Entity\":\"Sales\"}},\"Property\":\"Sales Amount\"}},"
          "\"queryRef\":\"Sales.Sales Amount\"}]}}},"
          "\"filterConfig\":{\"filters\":[{\"name\":\"visualFilter\",\"filter\":{"
          "\"From\":[{\"Name\":\"r\",\"Entity\":\"Reseller\"}],"
          "\"Where\":[{\"Condition\":{\"In\":{\"Expressions\":[{\"Column\":{\"Expression\":"
          "{\"SourceRef\":{\"Source\":\"r\"}},\"Property\":\"Business Type\"}}],"
          "\"Values\":[[{\"Literal\":{\"Value\":\"'Warehouse'\"}}]]}}}]}}]}}}" }
    };
    ReportLayout layout;

    if (read_split_layout(MEMBERS, sizeof(MEMBERS) / sizeof(MEMBERS[0]), &layout) != 0) {
        return;
    }

    check(layout.page_count == 1 && layout.pages[0].visual_count == 1,
        "the filtered visual is kept");
    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        const char *sql = layout.pages[0].visuals[0].sql;

        /* Both filters apply: the page's narrows every visual on it, the visual's only itself. */
        check(contains(sql, "WHERE ([d].[Fiscal Year] >= 2020)"),
            "the page filter reaches the WHERE clause, its typed literal written as T-SQL");
        check(contains(sql, "AND [r].[Business Type] IN ('Warehouse')"),
            "the visual's own filter is ANDed onto it");
    }
    report_layout_free(&layout);
}

/*
 * Power BI types its literals (2020L, 1.5D, 2.25M, datetime'...', true), and none of those parse as
 * T-SQL. Each is written in its T-SQL form, so the rendered question is a statement a T-SQL parser
 * (and a translation to the source tables) can read; a text literal is already T-SQL and is kept.
 */
static void test_writes_typed_literals_as_tsql(void)
{
    static const SplitMember MEMBERS[] = {
        { "Report/definition/pages/pages.json", "{\"pageOrder\":[\"p1\"]}" },
        { "Report/definition/pages/p1/page.json",
          "{\"name\":\"p1\",\"displayName\":\"Page 1\",\"filterConfig\":{\"filters\":[{"
          "\"name\":\"f\",\"filter\":{\"From\":[{\"Name\":\"s\",\"Entity\":\"Sales\"}],"
          "\"Where\":["
          "{\"Condition\":{\"Comparison\":{\"ComparisonKind\":1,\"Left\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Order Date\"}},"
          "\"Right\":{\"Literal\":{\"Value\":\"datetime'2020-01-01T00:00:00'\"}}}}},"
          "{\"Condition\":{\"Comparison\":{\"ComparisonKind\":2,\"Left\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Discount\"}},"
          "\"Right\":{\"Literal\":{\"Value\":\"1.5D\"}}}}},"
          "{\"Condition\":{\"Comparison\":{\"ComparisonKind\":3,\"Left\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Price\"}},"
          "\"Right\":{\"Literal\":{\"Value\":\"-2.25M\"}}}}},"
          "{\"Condition\":{\"Comparison\":{\"ComparisonKind\":0,\"Left\":{\"Column\":"
          "{\"Expression\":{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Returned\"}},"
          "\"Right\":{\"Literal\":{\"Value\":\"true\"}}}}},"
          "{\"Condition\":{\"In\":{\"Expressions\":[{\"Column\":{\"Expression\":"
          "{\"SourceRef\":{\"Source\":\"s\"}},\"Property\":\"Customer\"}}],"
          "\"Values\":[[{\"Literal\":{\"Value\":\"'O''Brien'\"}}],[{\"Literal\":{\"Value\":\"null\"}}]]}}}"
          "]}}]}}" },
        { "Report/definition/pages/p1/visuals/v1/visual.json",
          "{\"name\":\"v1\",\"visual\":{\"visualType\":\"card\",\"query\":{\"queryState\":{"
          "\"Values\":{\"projections\":[{\"field\":{\"Column\":{\"Expression\":{\"SourceRef\":"
          "{\"Entity\":\"Sales\"}},\"Property\":\"Price\"}},"
          "\"queryRef\":\"Sales.Price\"}]}}}}}" }
    };
    ReportLayout layout;

    if (read_split_layout(MEMBERS, sizeof(MEMBERS) / sizeof(MEMBERS[0]), &layout) != 0) {
        return;
    }

    check(layout.page_count == 1 && layout.pages[0].visual_count == 1,
        "the visual with typed literals is kept");
    if (layout.page_count == 1 && layout.pages[0].visual_count == 1) {
        const char *sql = layout.pages[0].visuals[0].sql;

        check(contains(sql, "([s].[Order Date] > '2020-01-01T00:00:00')"),
            "a datetime literal becomes its quoted text");
        check(contains(sql, "([s].[Discount] >= 1.5)"), "a double literal loses its D suffix");
        check(contains(sql, "([s].[Price] < -2.25)"), "a decimal literal loses its M suffix");
        check(contains(sql, "([s].[Returned] = 1)"), "true becomes 1");
        check(contains(sql, "IN ('O''Brien', NULL)"), "text stays quoted and null becomes NULL");
        check(!contains(sql, "datetime'") && !contains(sql, "true"),
            "no Power BI literal form survives");
    }
    report_layout_free(&layout);
}

/*
 * A visual carrying no query states no business question: a textbox or a shape is decoration, and
 * is dropped rather than recorded with an empty field list.
 */
static void test_a_split_visual_with_no_query_states_no_question(void)
{
    static const SplitMember MEMBERS[] = {
        { "Report/definition/pages/pages.json", "{\"pageOrder\":[\"p1\"]}" },
        { "Report/definition/pages/p1/page.json", "{\"name\":\"p1\",\"displayName\":\"Page 1\"}" },
        { "Report/definition/pages/p1/visuals/v1/visual.json",
          "{\"name\":\"v1\",\"visual\":{\"visualType\":\"textbox\",\"objects\":{}}}" }
    };
    ReportLayout layout;

    if (read_split_layout(MEMBERS, sizeof(MEMBERS) / sizeof(MEMBERS[0]), &layout) != 0) {
        return;
    }

    check(layout.page_count == 1, "the page is still read");
    if (layout.page_count == 1) {
        check(layout.pages[0].visual_count == 0, "a visual with no query is not recorded");
    }
    report_layout_free(&layout);
}

/* A file with neither report part is the one case that is a real failure. */
static void test_a_file_with_no_report_part_at_all_fails(void)
{
    static const SplitMember MEMBERS[] = {
        { "Version", "{}" }
    };
    const char *path = "build/test-fixture-split.pbix";
    ReportLayout layout;
    char error[512];

    if (write_pbix_split(path, MEMBERS, sizeof(MEMBERS) / sizeof(MEMBERS[0])) != 0) {
        fprintf(stderr, "FAIL: could not write the split test fixture\n");
        failures++;
        return;
    }

    error[0] = '\0';
    check(report_layout_read(path, &layout, error, sizeof(error)) != 0,
        "a file holding neither report part is reported as a failure");
    /* The older format's absence must not be explained as a missing DataModel, which would send a
     * reader after the wrong problem entirely. */
    check(!contains(error, "DataModel"),
        "the failure names the missing report part rather than the model");
    report_layout_free(&layout);
}

int main(void)
{
    test_extracts_pages_visuals_and_field_roles();
    test_renders_the_visuals_query_with_its_filter();
    test_renders_aggregations_measures_and_hierarchy_levels();
    test_folds_page_level_filters_into_every_visual();
    test_refuses_a_visual_whose_filter_it_cannot_translate();
    test_reports_a_projection_the_query_does_not_select();
    test_a_report_with_no_sections_is_not_an_error();
    test_reads_the_split_report_format();
    test_folds_split_format_filters_into_the_query();
    test_writes_typed_literals_as_tsql();
    test_a_split_visual_with_no_query_states_no_question();
    test_a_file_with_no_report_part_at_all_fails();

    remove("build/test-fixture.pbix");
    remove("build/test-fixture-split.pbix");

    /* The M-source resolver's own checks, folded into this suite's totals so one run covers both. */
    {
        int msource_checks = 0;
        failures += run_msource_tests(&msource_checks);
        checks += msource_checks;
    }

    if (failures > 0) {
        fprintf(stderr, "\n%d of %d checks FAILED\n", failures, checks);
        return EXIT_FAILURE;
    }
    printf("all %d checks passed\n", checks);
    return EXIT_SUCCESS;
}
