using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The HTTP(S) transport: for one iteration it drives the configured pagination strategy, fetching each page through
/// the shared executor and landing its raw body verbatim. It parses the body as JSON only to count records (for the
/// empty-page stop and the skip-empty policy), read the next cursor/id, and advance the watermark; the bytes written
/// are always the untouched response. Covers REST, GraphQL, and SOAP (all HTTP requests with a templated body).
/// </summary>
public sealed class HttpTransport : IAcquireTransport
{
    public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.Http;

    public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var request = fetch.Source.Request ?? throw new SqlFlowException("An HTTP api source requires a 'request' block.");
        var pagination = fetch.Source.Pagination;
        var http = fetch.Http ?? throw new SqlFlowException("The HTTP transport requires an initialized executor.");
        var allowStatuses = pagination.StopOnStatus is { } stop ? new HashSet<int> { stop } : null;

        var pageNumber = pagination.StartPage;
        var offset = 0;
        string? cursor = null;
        string? idAfter = fetch.Watermark.Before;
        Uri? nextLink = null;
        var maxPages = Math.Min(Math.Max(1, pagination.MaxPages), fetch.MaxPagesOverride ?? int.MaxValue);

        for (var page = 0; page < maxPages; page++)
        {
            var extraQuery = BuildPageQuery(pagination, pageNumber, offset, cursor, idAfter);

            // Body-carried paging: the page number is a template variable the request body renders, not a query
            // parameter. Bound on a clone so the landing context keeps naming the iteration, not the page.
            var vars = pagination.PageVariable is { } pageVariable
                ? fetch.Vars.Clone().WithString(pageVariable, pageNumber.ToString(CultureInfo.InvariantCulture))
                : fetch.Vars;

            HttpFetchResult result;
            Func<HttpRequestMessage> factory =
                pagination.Strategy == AcquirePaginationStrategy.LinkHeader && nextLink is { } linkUrl
                    ? () => HttpRequestBuilder.BuildForUrl(linkUrl, request, vars, fetch.Auth)
                    : () => HttpRequestBuilder.Build(fetch.Source.BaseUrl, request, vars, fetch.Auth, extraQuery);

            var probeRequest = fetch.Probe is not null ? factory() : null;
            var startTimestamp = Stopwatch.GetTimestamp();
            result = await http.SendAsync(factory, allowStatuses, request.ResponseCharset, ct).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

            fetch.Pages++;

            // A pagination sentinel status (e.g. 202 "no more reports") ends the loop without landing.
            if (pagination.StopOnStatus is { } sentinel && (int)result.Status == sentinel)
            {
                EmitProbe(fetch, page, probeRequest, result, elapsed, recordCount: 0, landedTo: null);
                fetch.Log.Log(RunLogLevel.Info, "pagination.stop", $"stop-on-status {sentinel} reached after page {page}.");
                break;
            }

            var json = TryParseJson(result);
            // An XML page is inspected the same way a JSON one is: enough to count records for the empty-page stop
            // and the skip-empty policy. The landed bytes stay the untouched response either way.
            var xml = json is null ? XmlPathReader.TryParse(result.Body, result.ContentType) : null;
            var recordCount = json is { } document
                ? JsonPathReader.CountRecords(document.RootElement, pagination.RecordsPath)
                : xml is { } element
                    ? XmlPathReader.CountRecords(element, pagination.RecordsPath)
                    : -1;

            var landed = await fetch.Landing.LandAsync(
                new LandedItem(result.Body, result.ContentType, page.ToString("0000", CultureInfo.InvariantCulture), recordCount, HeaderMap(result)),
                fetch.Vars, ct).ConfigureAwait(false);

            EmitProbe(fetch, page, probeRequest, result, elapsed, recordCount, landed?.Location);

            if (pagination.KeysetIdHeader is { } watermarkHeader)
            {
                // Body is a binary payload (no JSON to read): the record's id rides a response header, and the
                // monotonic keyset id is itself the resume watermark.
                fetch.Watermark.ObserveId(ReadHeader(result, watermarkHeader));
            }
            else if (json is not null)
            {
                fetch.Watermark.Observe(json.RootElement, pagination.RecordsPath);
            }

            if (!Advance(pagination, json, recordCount, ref pageNumber, ref offset, ref cursor, ref idAfter, ref nextLink, result))
            {
                break;
            }

            json?.Dispose();
        }
    }

    private static Dictionary<string, string> BuildPageQuery(
        AcquirePagination pagination, int pageNumber, int offset, string? cursor, string? idAfter)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (pagination.Strategy)
        {
            // A body-carried page is bound as a template variable instead; sending it as a query parameter as well
            // would put the page in two places at once, and a service that validates its query string rejects the
            // one it never declared.
            case AcquirePaginationStrategy.Page when pagination.PageVariable is null:
                query[pagination.PageParam] = pageNumber.ToString(CultureInfo.InvariantCulture);
                break;
            case AcquirePaginationStrategy.Offset:
                query[pagination.OffsetParam] = offset.ToString(CultureInfo.InvariantCulture);
                query[pagination.LimitParam] = pagination.Limit.ToString(CultureInfo.InvariantCulture);
                break;
            case AcquirePaginationStrategy.CursorBody when cursor is not null:
                query[pagination.CursorParam] = cursor;
                break;
            case AcquirePaginationStrategy.Keyset when !string.IsNullOrEmpty(idAfter):
                query[pagination.KeysetParam] = idAfter!;
                break;
        }

        return query;
    }

    private static bool Advance(
        AcquirePagination pagination,
        JsonDocument? json,
        int recordCount,
        ref int pageNumber,
        ref int offset,
        ref string? cursor,
        ref string? idAfter,
        ref Uri? nextLink,
        HttpFetchResult result)
    {
        switch (pagination.Strategy)
        {
            case AcquirePaginationStrategy.None:
                return false;

            case AcquirePaginationStrategy.Page:
                if (recordCount == 0)
                {
                    return false;
                }

                pageNumber++;
                return true;

            case AcquirePaginationStrategy.Offset:
                if (recordCount == 0)
                {
                    return false;
                }

                offset += pagination.Limit;
                return true;

            case AcquirePaginationStrategy.CursorBody:
                cursor = json is not null && pagination.CursorPath is { } path ? JsonPathReader.SelectValue(json.RootElement, path) : null;
                return !string.IsNullOrEmpty(cursor);

            case AcquirePaginationStrategy.LinkHeader:
                nextLink = result.NextLink is { } link && Uri.TryCreate(link, UriKind.Absolute, out var parsed) ? parsed : null;
                return nextLink is not null;

            case AcquirePaginationStrategy.Keyset:
            {
                // Header-sourced keyset: the record's id is a response header (the body is a binary file, so there
                // is no JSON to read). The empty-page/JSON guard below does not apply - a non-empty binary body is a
                // real record - so termination rests on the stop-on-status sentinel (handled before landing) and on
                // the header being absent or not advancing.
                string? next;
                if (pagination.KeysetIdHeader is { } headerName)
                {
                    next = ReadHeader(result, headerName);
                }
                else
                {
                    if (recordCount == 0 || json is null)
                    {
                        return false;
                    }

                    next = pagination.CursorPath is { } cursorPath
                        ? JsonPathReader.SelectValue(json.RootElement, cursorPath)
                        : pagination.KeysetIdPath is { } idPath
                            ? JsonPathReader.MaxColumn(json.RootElement, idPath, pagination.RecordsPath)
                            : null;
                }

                if (string.IsNullOrEmpty(next) || next == idAfter)
                {
                    return false;
                }

                idAfter = next;
                return true;
            }

            default:
                return false;
        }
    }

    private static JsonDocument? TryParseJson(HttpFetchResult result)
    {
        if (result.Body.Length == 0)
        {
            return null;
        }

        var contentType = result.ContentType?.ToLowerInvariant();
        var looksJson = contentType is not null && (contentType.Contains("json", StringComparison.Ordinal))
                        || result.Body[0] is (byte)'{' or (byte)'[';
        if (!looksJson)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EmitProbe(AcquireFetch fetch, int page, HttpRequestMessage? probeRequest, HttpFetchResult result, TimeSpan elapsed, int recordCount, string? landedTo)
    {
        if (fetch.Probe is null || probeRequest is null)
        {
            probeRequest?.Dispose();
            return;
        }

        var requestHeaders = probeRequest.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        fetch.Probe.Page(new AcquirePageProbe
        {
            Iteration = fetch.Iteration,
            Page = page,
            Method = probeRequest.Method.Method,
            Url = probeRequest.RequestUri?.ToString() ?? string.Empty,
            RequestHeaders = HeaderRedaction.Redact(requestHeaders),
            Status = (int)result.Status,
            ResponseHeaders = HeaderRedaction.Redact(HeaderMap(result)),
            ContentType = result.ContentType,
            Bytes = result.Body.Length,
            RecordCount = recordCount,
            DurationMs = Math.Round(elapsed.TotalMilliseconds, 1),
            BodyPreview = TransportProbe.Preview(result.Body),
            LandedTo = landedTo,
        });
        probeRequest.Dispose();
    }

    /// <summary>Reads a single response header (checking both the response and content header collections),
    /// returning the first value, or null when the header is absent. Used to advance a header-sourced keyset.</summary>
    private static string? ReadHeader(HttpFetchResult result, string headerName)
    {
        if (result.Headers.TryGetValues(headerName, out var values))
        {
            return values.FirstOrDefault();
        }

        return result.ContentHeaders is not null && result.ContentHeaders.TryGetValues(headerName, out var contentValues)
            ? contentValues.FirstOrDefault()
            : null;
    }

    private static Dictionary<string, string> HeaderMap(HttpFetchResult result)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in result.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (result.ContentHeaders is not null)
        {
            foreach (var (name, values) in result.ContentHeaders)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        return headers;
    }
}
