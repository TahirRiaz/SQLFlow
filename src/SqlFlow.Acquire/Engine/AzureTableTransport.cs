using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The Azure Storage Table transport: runs a (templated) OData filter against a table and lands the matched entities
/// as a single raw JSON array, preserving every property. Options (in <c>source.options</c>): <c>tableName</c>
/// (required) and an optional <c>filter</c> (OData, templated with the iteration variables) and <c>select</c>
/// (comma-separated columns). Authentication supports both modes: an explicit <c>connectionString</c> (secret) or
/// <c>accountUrl</c> + <c>sasToken</c> (secret), or - when neither secret is given - the ambient identity through the
/// shared <see cref="IAzureCredentialFactory"/> (managed identity on Azure, <c>az login</c> on a dev box, service
/// principal in CI). The ambient path is the default: an <c>accountUrl</c> (or the <c>sftp</c>-style base url) with no
/// secret authenticates with managed identity / az login, so a table source needs no stored secret.
/// </summary>
public sealed class AzureTableTransport : IAcquireTransport
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly IAzureCredentialFactory _credentials;

    public AzureTableTransport(IAzureCredentialFactory credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
    }

    public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.AzureTable;

    public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var options = fetch.Source.Options;
        var tableName = options.GetString("tableName", string.Empty);
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new SqlFlowException("An Azure Table source requires 'tableName' in source.options.");
        }

        var client = await BuildClientAsync(fetch, tableName, ct).ConfigureAwait(false);
        var filter = options.TryGetValue("filter", out var rawFilter) && !string.IsNullOrWhiteSpace(rawFilter)
            ? TemplateEngine.Render(rawFilter!, fetch.Vars)
            : null;
        var select = options.TryGetValue("select", out var rawSelect) && !string.IsNullOrWhiteSpace(rawSelect)
            ? rawSelect!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        var records = new List<Dictionary<string, object?>>();
        var startTimestamp = Stopwatch.GetTimestamp();
        await foreach (var entity in client.QueryAsync<TableEntity>(filter: filter, select: select, cancellationToken: ct).ConfigureAwait(false))
        {
            var record = new Dictionary<string, object?>(entity.Count, StringComparer.Ordinal);
            foreach (var (key, value) in entity)
            {
                record[key] = Normalize(value);
            }

            records.Add(record);
        }

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
        fetch.Pages++;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, Json);
        var landed = await fetch.Landing.LandAsync(
            new LandedItem(bytes, "application/json", "entities", records.Count, Headers: null),
            fetch.Vars, ct).ConfigureAwait(false);
        CaptureProbe(fetch, tableName, filter, select, bytes, records.Count, elapsed, landed?.Location);

        fetch.Log.Log(RunLogLevel.Info, "azuretable", $"queried {records.Count} entit(y/ies) from '{tableName}'.");
    }

    /// <summary>Records the entity query as a single debugger page: the OData filter/select as the "request", the
    /// entity count as the "response", and a bounded preview of the landed JSON array. Only the Test invoke probes.</summary>
    private static void CaptureProbe(
        AcquireFetch fetch, string tableName, string? filter, string[]? select, byte[] bytes, int recordCount,
        TimeSpan elapsed, string? landedTo)
    {
        if (fetch.Probe is null)
        {
            return;
        }

        var requestHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { ["table"] = tableName };
        if (filter is not null)
        {
            requestHeaders["filter"] = filter;
        }

        if (select is not null)
        {
            requestHeaders["select"] = string.Join(", ", select);
        }

        fetch.Probe.Page(new AcquirePageProbe
        {
            Iteration = fetch.Iteration,
            Page = 0,
            Method = "TABLE QUERY",
            Url = tableName,
            RequestHeaders = requestHeaders,
            Status = 200,
            ResponseHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["entities"] = recordCount.ToString(CultureInfo.InvariantCulture),
            },
            ContentType = "application/json",
            Bytes = bytes.Length,
            RecordCount = recordCount,
            DurationMs = Math.Round(elapsed.TotalMilliseconds, 1),
            BodyPreview = TransportProbe.Preview(bytes),
            LandedTo = landedTo,
        });
    }

    private async Task<TableClient> BuildClientAsync(AcquireFetch fetch, string tableName, CancellationToken ct)
    {
        var options = fetch.Source.Options;
        if (options.TryGetValue("connectionString", out var connRef) && !string.IsNullOrWhiteSpace(connRef))
        {
            var connectionString = await fetch.Secrets.ResolveAsync(connRef!, ct).ConfigureAwait(false);
            return new TableClient(connectionString, tableName);
        }

        var accountUrl = options.GetString("accountUrl", fetch.Source.BaseUrl);
        if (string.IsNullOrWhiteSpace(accountUrl))
        {
            throw new SqlFlowException(
                "An Azure Table source requires 'accountUrl' (or a base url) so the ambient identity can authenticate, "
                + "or a 'connectionString' / 'accountUrl'+'sasToken' secret in source.options.");
        }

        if (options.TryGetValue("sasToken", out var sasRef) && !string.IsNullOrWhiteSpace(sasRef))
        {
            var sas = await fetch.Secrets.ResolveAsync(sasRef!, ct).ConfigureAwait(false);
            // The SAS TableClient takes the table endpoint (account URL + table name) plus the SAS credential.
            var endpoint = new Uri($"{accountUrl.TrimEnd('/')}/{tableName}");
            return new TableClient(endpoint, new AzureSasCredential(sas));
        }

        // No explicit secret: authenticate with the ambient identity (managed identity on Azure, az login on a dev
        // box, service principal in CI) through the shared credential chain, the same as the raw landing store.
        return new TableClient(new Uri(accountUrl), tableName, _credentials.Create());
    }

    private static object? Normalize(object? value) => value switch
    {
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTimeOffset dto => dto.ToString("o"),
        DateTime dt => dt.ToString("o"),
        _ => value,
    };
}
