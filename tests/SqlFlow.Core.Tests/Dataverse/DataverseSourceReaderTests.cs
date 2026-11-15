using System.Data.Common;
using System.Net;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Dataverse;
using Xunit;

namespace SqlFlow.Tests.Dataverse;

/// <summary>
/// The Dataverse reader against a scripted environment: it pages a table through <c>@odata.nextLink</c> into typed rows,
/// waits out throttling for as long as the service asks, renews a refused token once, reports the service's own refusals,
/// never sends its token to another host, and reports a bounded read with nothing new as the clean no-op it is.
/// </summary>
public sealed class DataverseSourceReaderTests
{
    private static async Task<List<object?[]>> ReadAllAsync(DbDataReader reader)
    {
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            rows.Add(values.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    private static async Task<(IReadOnlyList<SourceColumn> Columns, List<object?[]> Rows, SourceReadResult Read)> RunAsync(DataverseSourceReader reader, SourceSpec spec)
    {
        var columns = await reader.GetColumnsAsync(spec);
        var read = await reader.OpenAsync(spec, columns);
        await using var data = read.Reader;
        var rows = await ReadAllAsync(data);
        await reader.CompleteAsync(spec);
        return (columns, rows, read);
    }

    [Fact]
    public async Task A_table_is_paged_through_its_next_links_into_typed_rows()
    {
        var service = DataverseTesting.Accounts(12);
        var spec = DataverseTesting.Spec(("pageSize", "5"));

        var (columns, rows, read) = await RunAsync(DataverseTesting.Reader(service), spec);

        Assert.Equal(
            ["accountid", "description", "modifiedon", "name", "numberofemployees", "parentaccountid", "revenue", "statecode", "versionnumber"],
            columns.Select(c => c.Name));
        Assert.Equal("DECIMAL(19, 4)", columns.Single(c => c.Name == "revenue").SqlType);
        Assert.Equal(12, rows.Count);
        Assert.Equal(DataverseTesting.Id(1), rows[0][0]);
        Assert.Equal(638_183_964_640_916_108L, rows[11][8]);
        Assert.Equal(DataverseTesting.Id(1), rows[4][5]);
        Assert.Null(rows[2][1]);

        var pages = service.RowRequests;
        Assert.Equal(3, pages.Count);
        Assert.All(pages, page =>
        {
            Assert.Equal("Bearer token-1", page.Authorization);
            Assert.Equal("odata.maxpagesize=5", page.Prefer);
            Assert.Equal("4.0", page.ODataVersion);
        });

        var manifest = Assert.Single(read.ProcessedFiles);
        Assert.Equal(12, manifest.Rows);
        Assert.Equal("account (3 page(s))", manifest.Name);
    }

    [Fact]
    public async Task Throttling_is_waited_out_for_as_long_as_the_service_asks()
    {
        var service = DataverseTesting.Accounts(3);
        service.Fail(FakeDataverse.IsRowRequest, (HttpStatusCode)429,
            FakeDataverse.ODataError("Number of requests exceeded the limit of 6000 over time window of 300 seconds.", "0x80072322"),
            retryAfter: "7", times: 2);
        var clock = new InstantClock();

        var (_, rows, _) = await RunAsync(DataverseTesting.Reader(service, clock), DataverseTesting.Spec());

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, clock.Waits.Count);
        Assert.All(clock.Waits, wait => Assert.True(wait >= TimeSpan.FromSeconds(7), $"waited {wait}"));
    }

    [Fact]
    public async Task Throttling_past_every_attempt_fails_naming_the_service_limit()
    {
        var service = DataverseTesting.Accounts(3);
        service.Fail(FakeDataverse.IsRowRequest, (HttpStatusCode)429,
            FakeDataverse.ODataError("Number of requests exceeded the limit of 6000.", "0x80072322"), retryAfter: "1", times: 10);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("maxAttempts", "3"))));

        Assert.Contains("failed after every retry", ex.Message, StringComparison.Ordinal);
        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
        Assert.Equal(3, service.RowRequests.Count);
    }

    [Fact]
    public async Task A_refused_token_is_renewed_once()
    {
        var service = DataverseTesting.Accounts(2);
        service.AcceptedToken = "token-2";
        var tokens = new ScriptedTokens();

        var (_, rows, _) = await RunAsync(DataverseTesting.Reader(service, tokens: tokens), DataverseTesting.Spec());

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, tokens.Refreshes);
    }

    [Fact]
    public async Task An_identity_the_environment_does_not_know_is_reported_as_such()
    {
        var service = DataverseTesting.Accounts(2);
        service.AcceptedToken = "never-issued";

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec()));

        Assert.Contains("HTTP 401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("application user", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_privilege_carries_the_services_own_message()
    {
        var service = DataverseTesting.Accounts(2);
        service.Fail(FakeDataverse.IsRowRequest, HttpStatusCode.Forbidden,
            FakeDataverse.ODataError("Principal user is missing prvReadAccount privilege.", "0x80042f09"));

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec()));

        Assert.Contains("missing prvReadAccount privilege (code 0x80042f09)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("security role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_table_is_reported_before_any_row_is_asked_for()
    {
        var service = DataverseTesting.Accounts(2);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("table", "acount"))));

        Assert.Contains("HTTP 404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("logical name", ex.Message, StringComparison.Ordinal);
        Assert.Empty(service.RowRequests);
    }

    [Fact]
    public async Task A_next_link_to_another_host_stops_the_read_without_sending_the_token_there()
    {
        var service = DataverseTesting.Accounts(4);
        service.NextLinkRewrite = link => link.Replace(FakeDataverse.Host, "attacker.example.com", StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("pageSize", "2"))));

        Assert.Contains("https://attacker.example.com", ex.Message, StringComparison.Ordinal);
        Assert.All(service.Requests, request => Assert.Equal(FakeDataverse.Host, request.Url.Host));
    }

    [Fact]
    public async Task A_page_that_points_back_at_itself_stops_the_read()
    {
        var service = DataverseTesting.Accounts(6);
        service.NextLinkRewrite = link => link.Contains("$skiptoken=4", StringComparison.Ordinal) ? link.Replace("$skiptoken=4", "$skiptoken=2", StringComparison.Ordinal) : link;

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("pageSize", "2"))));

        Assert.Contains("back at itself", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bounded_read_with_nothing_new_is_the_clean_no_op_at_the_schema_step()
    {
        var service = DataverseTesting.Accounts(3);
        var spec = DataverseTesting.Bounded(DataverseTesting.Spec(), "versionnumber", "638183964640916103", WatermarkKind.Whole);

        var ex = await Assert.ThrowsAsync<NoSourceFilesException>(() => DataverseTesting.Reader(service).GetColumnsAsync(spec));

        Assert.Equal(NoSourceFilesReason.NoneAfterWatermark, ex.Reason);
        Assert.Contains("nothing is new", ex.Message, StringComparison.Ordinal);
        Assert.Contains("versionnumber gt 638183964640916103", Uri.UnescapeDataString(service.RowRequests.Single().Url.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_relative_next_link_is_followed_from_the_page_it_came_from()
    {
        var service = DataverseTesting.Accounts(5);
        service.NextLinkRewrite = link => link[link.IndexOf("/api/", StringComparison.Ordinal)..];

        var (_, rows, _) = await RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("pageSize", "2")));

        Assert.Equal(5, rows.Count);
        Assert.Equal(3, service.RowRequests.Count);
    }

    [Fact]
    public async Task An_unbounded_read_of_an_empty_table_loads_nothing_rather_than_skipping()
    {
        var service = DataverseTesting.Accounts(0);

        var (_, rows, read) = await RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec());

        Assert.Empty(rows);
        Assert.Equal(0, Assert.Single(read.ProcessedFiles).Rows);
    }

    [Fact]
    public async Task A_bounded_read_asks_only_for_rows_past_the_bound_in_watermark_order()
    {
        var service = DataverseTesting.Accounts(10);
        var spec = DataverseTesting.Bounded(DataverseTesting.Spec(("pageSize", "3")), "versionnumber", "638183964640916098", WatermarkKind.Whole);

        var (_, rows, _) = await RunAsync(DataverseTesting.Reader(service), spec);

        Assert.Equal(8, rows.Count);
        Assert.Equal(Enumerable.Range(3, 8).Select(i => 638_183_964_640_916_096L + i), rows.Select(r => (long)r[8]!));
    }

    [Fact]
    public async Task Stopping_the_load_part_way_cancels_the_page_fetched_ahead()
    {
        var service = DataverseTesting.Accounts(9);
        var reader = DataverseTesting.Reader(service);
        var spec = DataverseTesting.Spec(("pageSize", "3"));
        var columns = await reader.GetColumnsAsync(spec);
        var read = await reader.OpenAsync(spec, columns);

        await using (var data = read.Reader)
        {
            Assert.True(await data.ReadAsync());
        }

        Assert.Empty(read.ProcessedFiles);
        Assert.True(service.RowRequests.Count <= 2);
    }

    [Fact]
    public async Task A_page_that_is_not_json_is_reported_with_how_it_began()
    {
        var service = DataverseTesting.Accounts(2);
        service.Fail(FakeDataverse.IsRowRequest, HttpStatusCode.OK, "<html>Service Unavailable</html>");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec()));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Contains("<html>Service Unavailable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_over_the_size_cap_says_which_options_to_change()
    {
        var service = DataverseTesting.Accounts(1);
        service.Fail(FakeDataverse.IsRowRequest, HttpStatusCode.OK, "{\"value\":[\"" + new string('x', (2 * 1024 * 1024) + 10) + "\"]}");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("maxPageMegabytes", "1"))));

        Assert.Contains("'pageSize'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'maxPageMegabytes'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_secret_reference_that_does_not_resolve_is_reported_without_signing_in()
    {
        var reader = new DataverseSourceReader(
            new SqlFlow.Core.Secrets.SecretResolver([new SqlFlow.Core.Secrets.EnvSecretProvider()]),
            ambient: null,
            new FakeDataverse(),
            tokens: null,
            new InstantClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var spec = DataverseTesting.Spec(
            ("tenantId", "00000000-0000-0000-0000-000000000001"),
            ("clientId", "00000000-0000-0000-0000-000000000002"),
            ("clientSecret", "${env:SQLFLOW_TEST_DATAVERSE_SECRET_THAT_IS_NOT_SET}"));

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => reader.GetColumnsAsync(spec));

        Assert.Contains("'clientSecret' names a reference that does not resolve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Labels_and_lookup_tables_arrive_from_the_annotations()
    {
        var service = DataverseTesting.Accounts(2);
        service.Attributes.Add(new FakeAttribute("ownerid", "OwnerType"));
        service.Rows[0]["statecode@label"] = "Active";
        service.Rows[0]["ownerid"] = DataverseTesting.Id(900);
        service.Rows[0]["ownerid@type"] = "team";
        service.Rows[0]["ownerid@label"] = "Sales team";

        var (columns, rows, _) = await RunAsync(DataverseTesting.Reader(service), DataverseTesting.Spec(("formattedValues", "true")));

        var names = columns.Select(c => c.Name).ToList();
        Assert.Equal("Active", rows[0][names.IndexOf("statecodename")]);
        Assert.Equal("team", rows[0][names.IndexOf("owneridtype")]);
        Assert.Equal("Sales team", rows[0][names.IndexOf("owneridname")]);
        Assert.Equal(DataverseTesting.Id(900), rows[0][names.IndexOf("ownerid")]);
        Assert.All(service.RowRequests, page => Assert.Contains("odata.include-annotations=\"*\"", page.Prefer, StringComparison.Ordinal));
    }
}
