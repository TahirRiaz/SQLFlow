using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Data;
using SqlFlow.Core.Dataverse;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Dataverse;

/// <summary>
/// The Microsoft Dataverse source reader (<c>source.type: dataverse</c>): reads one Power Apps table through the Dataverse
/// Web API into the file flow's load, so the engine's schema evolution, row-level watermark, load modes and typed view
/// apply to a Dataverse table as they do to files.
/// </summary>
/// <remarks>
/// <para>
/// Each run reads the table's metadata first, so its columns arrive typed (a GUID as uniqueidentifier, money as decimal,
/// a date-only column as date) and a column added or widened in Power Apps reaches the target through the engine's
/// <c>widen</c> evolution with no change to the flow. The rows are then paged through <c>@odata.nextLink</c>, in the
/// watermark column's order when the flow declares one, with the engine's bound pushed into <c>$filter</c> so only new
/// rows cross the wire. The next page is fetched while the current one loads, and memory is bounded by two pages.
/// </para>
/// <para>
/// The schema stage fetches the metadata and the first page and parks them for the load stage, the way the DuckDB reader
/// parks its prepared connection: a run asks the service once, and a bounded read that finds nothing new is reported at
/// the schema stage as the clean no-op it is, before anything on the target changes. What is parked holds no connection,
/// only managed memory, so a plan that never loads leaves nothing open behind it.
/// </para>
/// </remarks>
public sealed class DataverseSourceReader : ISourceReader
{
    // A run of empty pages that keeps pointing on is a service fault, not data; stop rather than page forever.
    private const int MaxConsecutiveEmptyPages = 100;

    private readonly ISecretResolver _secrets;
    private readonly IAzureCredentialFactory? _ambient;
    private readonly HttpMessageHandler? _handler;
    private readonly Func<DataverseSourceOptions, IDataverseTokenProvider>? _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<SourceSpec, ParkedSlot> _parked = new();
    private readonly ConcurrentDictionary<int, HttpClient> _clients = new();
    private readonly ConcurrentDictionary<string, TokenCredential> _credentials = new(StringComparer.Ordinal);

    /// <summary>Builds the reader the engine registers: secrets through the run's resolver, the ambient Azure identity
    /// from the host's <c>SQLFLOW_AZURE_AUTH</c>, the real network and clock.</summary>
    public DataverseSourceReader(ISecretResolver secrets, IAzureCredentialFactory ambient, ILogger<DataverseSourceReader>? logger = null, TimeProvider? time = null)
        : this(secrets, ambient, handler: null, tokens: null, time ?? TimeProvider.System, logger ?? (ILogger)NullLogger.Instance)
    {
    }

    /// <summary>The seam the tests use: a scripted service behind <paramref name="handler"/>, a token source that needs no
    /// identity, and a clock that does not really wait.</summary>
    internal DataverseSourceReader(
        ISecretResolver secrets,
        IAzureCredentialFactory? ambient,
        HttpMessageHandler? handler,
        Func<DataverseSourceOptions, IDataverseTokenProvider>? tokens,
        TimeProvider time,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _secrets = secrets;
        _ambient = ambient;
        _handler = handler;
        _tokens = tokens;
        _time = time;
        _logger = logger;
    }

    public bool CanHandle(string sourceType) => DataverseSourceOptions.IsDataverse(sourceType);

    public async Task<IReadOnlyList<SourceColumn>> GetColumnsAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var session = await PrepareAsync(source, ct).ConfigureAwait(false);

        // A re-planned run displaces what an earlier plan parked; its first page is released here.
        var slot = _parked.GetOrCreateValue(source);
        Interlocked.Exchange(ref slot.Session, session)?.Dispose();
        return session.SourceColumns;
    }

    public async Task<SourceReadResult> OpenAsync(SourceSpec source, IReadOnlyList<SourceColumn> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);

        var session = TakeParked(source) ?? await PrepareAsync(source, ct).ConfigureAwait(false);
        try
        {
            var planned = session.SourceColumns.Select(c => c.Name);
            if (!planned.SequenceEqual(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException(
                    $"The Dataverse table '{session.Plan.Table.LogicalName}' was planned with the columns ({string.Join(", ", planned)}) "
                    + $"but opened for ({string.Join(", ", columns.Select(c => c.Name))}); the table changed between the two steps of one run. Run it again.");
            }
        }
        catch
        {
            session.Dispose();
            throw;
        }

        var manifest = new List<ProcessedFile>();
        var names = session.Plan.Columns.Select(c => c.Name).ToArray();
        var types = session.Plan.Columns.Select(c => c.ClrType).ToArray();
        var reader = new StreamingDataReader(names, types, ReadRowsAsync(session, manifest, ct).GetAsyncEnumerator(ct));
        return new SourceReadResult { Reader = reader, ProcessedFiles = manifest };
    }

    /// <summary>Releases a session a plan parked and no load took (a schema-only plan, or a run that failed between the
    /// two steps). A normal run's session was taken by <see cref="OpenAsync"/>; this is a no-op then.</summary>
    public Task CompleteAsync(SourceSpec source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        TakeParked(source)?.Dispose();
        return Task.CompletedTask;
    }

    private Session? TakeParked(SourceSpec source)
        => _parked.TryGetValue(source, out var slot) ? Interlocked.Exchange(ref slot.Session, null) : null;

    private async Task<Session> PrepareAsync(SourceSpec source, CancellationToken ct)
    {
        var options = DataverseSourceOptions.Parse(source);
        var tokens = _tokens?.Invoke(options) ?? await TokenProviderAsync(options, ct).ConfigureAwait(false);
        var executor = new HttpExecutor(
            HttpClientFor(options.TimeoutSeconds),
            new RetryPolicy(
                new AcquireRetry
                {
                    MaxAttempts = options.MaxAttempts,
                    BaseDelayMs = 1000,
                    MaxDelayMs = options.MaxRetryDelaySeconds * 1000,
                    HonorRetryAfter = true,
                },
                _time),
            new RateLimiter(0, _time),
            new UrlGuard([options.Environment.Host]),
            (long)options.MaxPageMegabytes * 1024 * 1024,
            _time,
            $"Lower the source option '{DataverseSourceOptions.PageSizeOption}' (rows per page), or raise '{DataverseSourceOptions.MaxPageMegabytesOption}'.");
        var client = new DataverseClient(executor, tokens, options.Environment);

        var metadata = await DataverseMetadataReader.ReadAsync(client, options, ct).ConfigureAwait(false);
        var plan = DataverseReadPlan.Build(metadata, options);
        var first = await FetchPageAsync(client, plan, plan.FirstPage, ct).ConfigureAwait(false);

        if (plan.Bounded && first.Count == 0 && first.NextLink is null)
        {
            first.Dispose();
            var bound = options.WatermarkBound!;
            throw new NoSourceFilesException(
                NoSourceFilesReason.NoneAfterWatermark,
                $"no row of the Dataverse table '{options.Table}' has {bound.Column} past {bound.CanonicalValue}; nothing is new since the last load.");
        }

        var columns = plan.Columns.Select(c => new SourceColumn
        {
            Name = c.Name,
            Type = c.ClrType,
            SqlType = c.SqlType,
            IsNullable = true,
        }).ToList();

        return new Session(client, plan, first, columns);
    }

    private async IAsyncEnumerable<object?[]> ReadRowsAsync(Session session, List<ProcessedFile> manifest, [EnumeratorCancellation] CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var plan = session.Plan;
        var page = session.TakeFirstPage();
        Task<Page>? ahead = null;
        long rows = 0;
        var pages = 0;
        var emptyInARow = 0;
        try
        {
            while (true)
            {
                pages++;
                if (page.NextLink is { } next)
                {
                    // Fetch the next page while this one loads, so the read waits on the service and the database at
                    // once rather than in turn. At most two pages are ever held.
                    ahead = FetchPageAsync(session.Client, plan, next, stop.Token);
                }

                foreach (var row in page.Rows)
                {
                    yield return DataverseValues.Row(row, plan.Columns, plan.Table.LogicalName, plan.PrimaryIdProperty);
                    rows++;
                }

                emptyInARow = page.Count == 0 ? emptyInARow + 1 : 0;
                if (emptyInARow > MaxConsecutiveEmptyPages)
                {
                    throw new SqlFlowException(
                        $"The Dataverse table '{plan.Table.LogicalName}' answered {emptyInARow} empty pages in a row, each pointing to another; "
                        + "the read stops rather than page forever.");
                }

                var current = page.NextLink;
                page.Dispose();
                if (ahead is null)
                {
                    break;
                }

                // Detached before it is awaited: a page that fails to arrive is this read's failure, not one to clean
                // up after, so the finally below never waits on it a second time.
                var arriving = ahead;
                ahead = null;
                page = await arriving.ConfigureAwait(false);
                if (page.NextLink is { } following && following == current)
                {
                    page.Dispose();
                    throw new SqlFlowException(
                        $"The Dataverse table '{plan.Table.LogicalName}' pointed page {pages + 1} back at itself ({following}); the read stops rather than loop.");
                }
            }

            manifest.Add(new ProcessedFile
            {
                Name = $"{plan.Table.LogicalName} ({pages} page(s))",
                Path = new Uri(plan.FirstPage.GetLeftPart(UriPartial.Path)).AbsoluteUri,
                Rows = rows,
                Columns = plan.Columns.Count,
            });
        }
        finally
        {
            page.Dispose();
            if (ahead is not null)
            {
                // The load stopped before this page was needed (it failed, or it was cancelled): cancel the fetch ahead
                // and wait for it, so nothing outlives the read. Its own outcome no longer decides anything, so it is
                // logged rather than allowed to replace the failure that stopped the load.
                await stop.CancelAsync().ConfigureAwait(false);
                try
                {
                    (await ahead.ConfigureAwait(false)).Dispose();
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("The Dataverse page fetched ahead for table '{Table}' was cancelled with the read.", plan.Table.LogicalName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The Dataverse page fetched ahead for table '{Table}' failed after the read had already stopped.", plan.Table.LogicalName);
                }
            }
        }
    }

    private static async Task<Page> FetchPageAsync(DataverseClient client, DataverseReadPlan plan, Uri url, CancellationToken ct)
    {
        var what = $"a page of the table '{plan.Table.LogicalName}'";
        var document = await client.GetJsonAsync(url, plan.Prefer, what, ct).ConfigureAwait(false);
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new SqlFlowException($"Reading {what}: the Dataverse answer has no 'value' list of rows.");
            }

            // The service writes the next link absolute; OData also allows one relative to the page it came from, which is
            // resolved against that page. Either way the client sends it only to the environment's own host.
            Uri? next = null;
            if (root.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind != JsonValueKind.Null)
            {
                if (link.ValueKind != JsonValueKind.String || !Uri.TryCreate(url, link.GetString(), out next))
                {
                    throw new SqlFlowException($"Reading {what}: the Dataverse answer's @odata.nextLink is not a URL ({link.GetRawText()}).");
                }
            }

            return new Page(document, value, next);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private HttpClient HttpClientFor(int timeoutSeconds)
        => _clients.GetOrAdd(timeoutSeconds, seconds =>
        {
            if (_handler is null)
            {
                // The shared hardened client: keep-alive probes, address racing, decompression, a stable user agent.
                return HttpClientBuilder.Build(new AcquireReliability { TimeoutSeconds = seconds });
            }

            return new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(seconds) };
        });

    private async Task<IDataverseTokenProvider> TokenProviderAsync(DataverseSourceOptions options, CancellationToken ct)
    {
        var environment = options.Environment.GetLeftPart(UriPartial.Authority);
        if (options.ServicePrincipal is { } principal)
        {
            var tenant = await ResolveAsync(principal.TenantId, DataverseSourceOptions.TenantIdOption, ct).ConfigureAwait(false);
            var clientId = await ResolveAsync(principal.ClientId, DataverseSourceOptions.ClientIdOption, ct).ConfigureAwait(false);
            var secret = await ResolveAsync(principal.ClientSecretReference, DataverseSourceOptions.ClientSecretOption, ct).ConfigureAwait(false);

            // One credential per principal and authority, kept across runs so Azure.Identity's token cache serves the
            // next run too. The key holds a hash of the secret, never the secret: a rotated secret gets a new credential.
            var authority = options.AuthorityHost ?? AzureAuthorityHosts.AzurePublicCloud;
            var key = $"{tenant}|{clientId}|{authority}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))}";
            var credential = _credentials.GetOrAdd(key, _ => new ClientSecretCredential(
                tenant, clientId, secret, new ClientSecretCredentialOptions { AuthorityHost = authority }));
            return new DataverseTokenProvider(credential, options.Scope, $"the service principal {clientId}", environment, _time);
        }

        var ambient = _ambient ?? throw new SqlFlowException(
            $"The dataverse source names no service principal, and this host has no Azure identity to sign in with. "
            + $"Set '{DataverseSourceOptions.TenantIdOption}', '{DataverseSourceOptions.ClientIdOption}' and '{DataverseSourceOptions.ClientSecretOption}'.");
        return new DataverseTokenProvider(ambient.Create(), options.Scope, $"the host's Azure identity ({AzureAuth.Mode()})", environment, _time);
    }

    private async Task<string> ResolveAsync(string value, string option, CancellationToken ct)
    {
        if (!SourceLocations.NamesReference(value))
        {
            return value;
        }

        string resolved;
        try
        {
            resolved = (await _secrets.ResolveAsync(value, ct).ConfigureAwait(false)).Trim();
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"The dataverse source option '{option}' names a reference that does not resolve: {ex.Message}", ex);
        }

        return resolved.Length > 0
            ? resolved
            : throw new SqlFlowException($"The dataverse source option '{option}' names a reference that resolves to nothing.");
    }

    private sealed class ParkedSlot
    {
        public Session? Session;
    }

    /// <summary>One run's read: the client, the plan, the first page until the load takes it, and the columns reported.</summary>
    private sealed class Session(DataverseClient client, DataverseReadPlan plan, Page firstPage, IReadOnlyList<SourceColumn> columns) : IDisposable
    {
        private Page? _firstPage = firstPage;

        public DataverseClient Client { get; } = client;

        public DataverseReadPlan Plan { get; } = plan;

        public IReadOnlyList<SourceColumn> SourceColumns { get; } = columns;

        public Page TakeFirstPage()
            => Interlocked.Exchange(ref _firstPage, null)
               ?? throw new InvalidOperationException("The first page of a Dataverse read was taken twice.");

        public void Dispose() => Interlocked.Exchange(ref _firstPage, null)?.Dispose();
    }

    /// <summary>One page of rows, holding the parsed document its rows point into until it is disposed.</summary>
    private sealed class Page(JsonDocument document, JsonElement rows, Uri? nextLink) : IDisposable
    {
        private JsonDocument? _document = document;

        public IEnumerable<JsonElement> Rows => rows.EnumerateArray();

        public int Count { get; } = rows.GetArrayLength();

        public Uri? NextLink { get; } = nextLink;

        public void Dispose() => Interlocked.Exchange(ref _document, null)?.Dispose();
    }
}
