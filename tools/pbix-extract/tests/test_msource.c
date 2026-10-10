/*
 * The M-expression source resolver: which Power Query shapes resolve to a physical warehouse object
 * and, just as importantly, which ones correctly refuse to.
 *
 * The refusals carry the weight here. A wrongly resolved table silently points a report's whole
 * consumption lineage at an object it never read, which is worse than no lineage at all because it
 * looks complete. So every partial or ambiguous shape is asserted to stay unresolved rather than be
 * guessed at.
 */

#include "test_msource.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "msource.h"

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

static void test_resolves_a_plain_sql_database_source(void)
{
    const char *m =
        "let\n"
        "    Source = Sql.Database(\"dwh.internal\", \"OdsDb\"),\n"
        "    arc_Sales = Source{[Schema=\"arc\",Item=\"Sales\"]}[Data]\n"
        "in\n"
        "    arc_Sales";
    MSourceResolution r;

    check(msource_resolve(m, &r) == 0, "a well-formed source resolves without error");
    check(r.resolved == 1, "a Sql.Database source with schema/item navigation resolves");
    check_str(r.server, "dwh.internal", "the server literal is kept verbatim");
    check_str(r.database, "OdsDb", "the database is the call's second argument");
    check_str(r.schema, "arc", "the schema comes from the navigation record");
    check_str(r.item, "Sales", "the item comes from the navigation record");
    msource_free(&r);
}

static void test_tolerates_intervening_steps_and_formatting(void)
{
    /* PowerBI routinely inserts transform steps between the source and the table the report sees,
     * and its formatter's whitespace varies, so neither position nor spacing can be assumed. */
    const char *m =
        "let\n"
        "    Source =\n"
        "        Sql.Database(  \"tcp:box.database.windows.net,1433\"  ,  \"Warehouse\"  ),\n"
        "    Navigation = Source{[ Schema = \"dbo\" , Item = \"FactSales\" ]}[Data],\n"
        "    #\"Changed Type\" = Table.TransformColumnTypes(Navigation,{{\"Amount\", type number}}),\n"
        "    #\"Filtered Rows\" = Table.SelectRows(#\"Changed Type\", each [Amount] > 0)\n"
        "in\n"
        "    #\"Filtered Rows\"";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 1, "extra steps and loose spacing do not defeat resolution");
    check_str(r.server, "tcp:box.database.windows.net,1433", "a full connection literal survives");
    check_str(r.schema, "dbo", "spaces around the record fields are tolerated");
    check_str(r.item, "FactSales", "the item is read through the spacing");
    msource_free(&r);
}

static void test_an_excel_source_is_unresolved_and_named(void)
{
    /* The one real sample on file is Excel-backed, so this is the shape most likely to be met. */
    const char *m =
        "let\n"
        "    Source = Excel.Workbook(File.Contents(\"<redacted>\"), null, true),\n"
        "    Sales_Table = Source{[Item=\"Sales\",Kind=\"Table\"]}[Data]\n"
        "in\n"
        "    Sales_Table";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 0, "an Excel-backed table does not resolve to a warehouse object");
    check_str(r.unresolved_shape, "Excel.Workbook", "the shape that defeated resolution is named");
    /* An Item= field IS present here. Reading it without a database would invent a target. */
    check(r.item == NULL, "a partial match yields nothing rather than a guess");
    msource_free(&r);
}

static void test_a_native_query_source_is_unresolved(void)
{
    /* Sql.Database(server, db, [Query="..."]) names its objects inside SQL text, not a schema/item
     * pair. Resolving it would need the query parsed, which this matcher deliberately does not do. */
    const char *m =
        "let\n"
        "    Source = Sql.Database(\"dwh\", \"OdsDb\", [Query=\"select * from arc.Sales\"])\n"
        "in\n"
        "    Source";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 0, "a native-query source stays unresolved rather than half-resolved");
    msource_free(&r);
}

static void test_sql_databases_plural_is_not_mistaken_for_the_singular(void)
{
    /* Sql.Databases("server") takes no database argument; its database is selected downstream. A
     * prefix match would read the server as the database and resolve to a fabricated object. */
    const char *m =
        "let\n"
        "    Source = Sql.Databases(\"dwh\"),\n"
        "    Db = Source{[Name=\"OdsDb\"]}[Data],\n"
        "    Nav = Db{[Schema=\"arc\",Item=\"Sales\"]}[Data]\n"
        "in\n"
        "    Nav";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 0, "Sql.Databases is not resolved as though it were Sql.Database");
    check_str(r.unresolved_shape, "Sql.Databases", "the plural form is named as the shape");
    msource_free(&r);
}

static void test_a_nested_first_argument_is_not_read_as_the_server(void)
{
    /* The server here is produced by a call, not a literal. Reading the inner call's literal would
     * attribute the table to whatever string that call happened to carry. */
    const char *m =
        "let\n"
        "    Source = Sql.Database(GetServer(\"prod\"), \"OdsDb\"),\n"
        "    Nav = Source{[Schema=\"arc\",Item=\"Sales\"]}[Data]\n"
        "in\n"
        "    Nav";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 0, "a computed server argument is not resolved from a nested literal");
    msource_free(&r);
}

static void test_a_doubled_quote_inside_a_literal_is_unescaped(void)
{
    const char *m =
        "let\n"
        "    Source = Sql.Database(\"dwh\", \"Odd\"\"Name\"),\n"
        "    Nav = Source{[Schema=\"arc\",Item=\"Sales\"]}[Data]\n"
        "in\n"
        "    Nav";
    MSourceResolution r;

    msource_resolve(m, &r);
    check(r.resolved == 1, "a literal containing an escaped quote still resolves");
    check_str(r.database, "Odd\"Name", "M's doubled quote is collapsed to one");
    msource_free(&r);
}

static void test_degenerate_input_is_safe(void)
{
    MSourceResolution r;

    check(msource_resolve(NULL, &r) == 0, "a NULL expression is not an error");
    check(r.resolved == 0, "a NULL expression resolves to nothing");
    msource_free(&r);

    msource_resolve("", &r);
    check(r.resolved == 0, "an empty expression resolves to nothing");
    msource_free(&r);

    /* Unterminated literals are exactly what a truncated or hand-edited file produces. */
    msource_resolve("let Source = Sql.Database(\"dwh", &r);
    check(r.resolved == 0, "an unterminated literal does not resolve");
    msource_free(&r);

    msource_resolve("Sql.Database(", &r);
    check(r.resolved == 0, "a truncated call does not resolve");
    msource_free(&r);

    msource_free(NULL);
    check(1, "freeing NULL is safe");
}

static void test_an_unknown_shape_reports_no_name(void)
{
    MSourceResolution r;

    msource_resolve("let Source = SomeVendor.Connect(\"x\") in Source", &r);
    check(r.resolved == 0, "an unrecognized connector does not resolve");
    check(r.unresolved_shape == NULL, "an unrecognized connector names no known shape");
    msource_free(&r);
}

int run_msource_tests(int *out_checks)
{
    test_resolves_a_plain_sql_database_source();
    test_tolerates_intervening_steps_and_formatting();
    test_an_excel_source_is_unresolved_and_named();
    test_a_native_query_source_is_unresolved();
    test_sql_databases_plural_is_not_mistaken_for_the_singular();
    test_a_nested_first_argument_is_not_read_as_the_server();
    test_a_doubled_quote_inside_a_literal_is_unescaped();
    test_degenerate_input_is_safe();
    test_an_unknown_shape_reports_no_name();

    if (out_checks != NULL) {
        *out_checks = checks;
    }
    return failures;
}
