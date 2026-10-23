using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Azure.Core.Pipeline;
using SqlFlow.Core;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// One hierarchical-namespace (ADLS Gen2) storage account in memory, served over both of its endpoints from the same
/// files: the Blob REST API on <c>&lt;account&gt;.blob.core.windows.net</c> (what the Azure Blob SDK sends) and the Data
/// Lake REST API on <c>&lt;account&gt;.dfs.core.windows.net</c> (what the storage layer's Data Lake client sends). As on
/// the real service, a file's parent directories exist as directories, the Blob endpoint shows a directory as a zero-length
/// blob carrying <c>hdi_isfolder=true</c> (and as a listing prefix when it has children), and the Data Lake endpoint lists
/// it with <c>isDirectory</c>. Each endpoint can be made to refuse this client the way a network does: a firewall rejection
/// (403 <c>AuthorizationFailure</c>), a name that does not resolve, or a refused connection.
/// </summary>
internal sealed class FakeStorageAccount : HttpMessageHandler
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SortedDictionary<string, Entry>> _containers = new(StringComparer.Ordinal);
    private long _clock;
    private long _etags;

    public FakeStorageAccount(string account)
    {
        Account = account;
    }

    public string Account { get; }

    public string BlobHost => $"{Account}.blob.core.windows.net";

    public string DfsHost => $"{Account}.dfs.core.windows.net";

    /// <summary>How the Blob endpoint treats this client.</summary>
    public EndpointBehavior Blob { get; set; } = EndpointBehavior.Serve;

    /// <summary>How the Data Lake endpoint treats this client.</summary>
    public EndpointBehavior Dfs { get; set; } = EndpointBehavior.Serve;

    /// <summary>The Data Lake listing's page size, so continuation is exercised.</summary>
    public int DfsPageSize { get; set; } = 1000;

    /// <summary>When set, the next Data Lake or Blob read of the whole file breaks its body after this many bytes.</summary>
    public int? BreakNextReadAfter { get; set; }

    /// <summary>When set, Data Lake appends fail with this status (and code) instead of storing data.</summary>
    public (int Status, string Code)? FailAppends { get; set; }

    /// <summary>When set, the next request to the named endpoint answers 503 once (a retriable failure).</summary>
    public string? BusyOnceHost { get; set; }

    /// <summary>Every request, in order.</summary>
    public List<Recorded> Requests { get; } = [];

    public HttpClientTransport Transport => new(this);

    public int RequestsTo(string host) { lock (_gate) { return Requests.Count(r => r.Host == host); } }

    // ---- Arranging state --------------------------------------------------------------------------------------------

    public void CreateContainer(string container)
    {
        lock (_gate)
        {
            _containers.TryAdd(container, new SortedDictionary<string, Entry>(StringComparer.Ordinal));
        }
    }

    public void PutFile(string container, string path, byte[] content, bool stampMd5 = true)
    {
        lock (_gate)
        {
            var files = _containers[container];
            EnsureParents(files, path);
            files[path] = new Entry(false, content, stampMd5 ? ContentHash.Md5(content) : null, Tick(), NewETag());
        }
    }

    public void PutFile(string container, string path, string content, bool stampMd5 = true)
        => PutFile(container, path, Encoding.UTF8.GetBytes(content), stampMd5);

    public void PutDirectory(string container, string path)
    {
        lock (_gate)
        {
            var files = _containers[container];
            EnsureParents(files, path);
            files.TryAdd(path, Directory());
        }
    }

    public byte[]? Content(string container, string path)
    {
        lock (_gate)
        {
            return _containers.TryGetValue(container, out var files) && files.TryGetValue(path, out var entry) && !entry.IsDirectory
                ? entry.Data
                : null;
        }
    }

    public byte[]? StoredMd5(string container, string path)
    {
        lock (_gate)
        {
            return _containers[container].TryGetValue(path, out var entry) ? entry.Md5 : null;
        }
    }

    public bool Exists(string container, string path)
    {
        lock (_gate)
        {
            return _containers.TryGetValue(container, out var files) && files.ContainsKey(path);
        }
    }

    public bool ContainerExists(string container)
    {
        lock (_gate)
        {
            return _containers.ContainsKey(container);
        }
    }

    /// <summary>Changes a file's content behind a reader's back (a new entity tag).</summary>
    public void Modify(string container, string path, byte[] content) => PutFile(container, path, content);

    // ---- Serving ----------------------------------------------------------------------------------------------------

    // The storage layer's synchronous stream members send synchronously; the fake answers them the same way.
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            Requests.Add(new Recorded(host, request.Method.Method, request.RequestUri.AbsolutePath, request.RequestUri.Query,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)));
        }

        var isDfs = host.Equals(DfsHost, StringComparison.OrdinalIgnoreCase);
        var isBlob = host.Equals(BlobHost, StringComparison.OrdinalIgnoreCase);
        if (!isDfs && !isBlob)
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host is known. ({host})", new SocketException((int)SocketError.HostNotFound));
        }

        switch (isDfs ? Dfs : Blob)
        {
            case EndpointBehavior.RejectNetwork:
                return Error(isDfs, request, HttpStatusCode.Forbidden, "AuthorizationFailure", "This request is not authorized to perform this operation.");
            case EndpointBehavior.Unresolvable:
                throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host is known. ({host})", new SocketException((int)SocketError.HostNotFound));
            case EndpointBehavior.Refused:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.", new SocketException((int)SocketError.ConnectionRefused));
            case EndpointBehavior.ServerError:
                return Error(isDfs, request, HttpStatusCode.InternalServerError, "InternalError", "The server encountered an internal error.");
        }

        lock (_gate)
        {
            if (BusyOnceHost is { } busy && busy.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                BusyOnceHost = null;
                return Error(isDfs, request, HttpStatusCode.ServiceUnavailable, "ServerBusy", "The server is currently unable to receive requests.");
            }

            var segments = request.RequestUri.AbsolutePath.TrimStart('/').Split('/');
            var container = Uri.UnescapeDataString(segments[0]);
            var path = segments.Length > 1 ? string.Join('/', segments.Skip(1).Select(Uri.UnescapeDataString)) : string.Empty;
            var query = Query(request.RequestUri);
            return isDfs ? ServeDfs(request, container, path, query, body) : ServeBlob(request, container, path, query, body);
        }
    }

    private HttpResponseMessage ServeDfs(HttpRequestMessage request, string container, string path, Dictionary<string, string> query, byte[] body)
    {
        var method = request.Method.Method;
        if (path.Length == 0)
        {
            switch (method)
            {
                case "HEAD":
                    return _containers.ContainsKey(container) ? Ok(HttpStatusCode.OK) : Error(true, request, HttpStatusCode.NotFound, "FilesystemNotFound", "The specified filesystem does not exist.");
                case "PUT":
                    return _containers.TryAdd(container, new SortedDictionary<string, Entry>(StringComparer.Ordinal))
                        ? Ok(HttpStatusCode.Created)
                        : Error(true, request, HttpStatusCode.Conflict, "FilesystemAlreadyExists", "The specified filesystem already exists.");
                case "GET":
                    return ListPaths(request, container, query);
            }
        }

        if (!_containers.TryGetValue(container, out var files))
        {
            return Error(true, request, HttpStatusCode.NotFound, "FilesystemNotFound", "The specified filesystem does not exist.");
        }

        files.TryGetValue(path, out var entry);
        switch (method)
        {
            case "HEAD":
            {
                if (entry is null)
                {
                    return Error(true, request, HttpStatusCode.NotFound, "PathNotFound", "The specified path does not exist.");
                }

                var response = Ok(HttpStatusCode.OK, Array.Empty<byte>());
                response.Content.Headers.ContentLength = entry.IsDirectory ? 0 : entry.Data.Length;
                response.Headers.TryAddWithoutValidation("x-ms-resource-type", entry.IsDirectory ? "directory" : "file");
                Stamp(response, entry);
                return response;
            }

            case "GET":
                return entry is null || entry.IsDirectory
                    ? Error(true, request, HttpStatusCode.NotFound, "PathNotFound", "The specified path does not exist.")
                    : Read(request, entry, dfs: true);

            case "PUT":
            {
                if (query.GetValueOrDefault("resource") != "file")
                {
                    return Error(true, request, HttpStatusCode.BadRequest, "InvalidQueryParameterValue", "Only file creation is served.");
                }

                if (request.Headers.TryGetValues("If-None-Match", out var none) && none.Contains("*") && entry is not null)
                {
                    return Error(true, request, HttpStatusCode.Conflict, "PathAlreadyExists", "The specified path already exists.");
                }

                EnsureParents(files, path);
                files[path] = new Entry(false, [], null, Tick(), NewETag());
                return Ok(HttpStatusCode.Created);
            }

            case "PATCH":
            {
                if (entry is null || entry.IsDirectory)
                {
                    return Error(true, request, HttpStatusCode.NotFound, "PathNotFound", "The specified path does not exist.");
                }

                var position = long.Parse(query["position"], CultureInfo.InvariantCulture);
                if (query["action"] == "append")
                {
                    if (FailAppends is { } failure)
                    {
                        return Error(true, request, (HttpStatusCode)failure.Status, failure.Code, "Append refused by the test.");
                    }

                    entry.Pending[position] = body;
                    return Ok(HttpStatusCode.Accepted);
                }

                if (query["action"] == "flush")
                {
                    var data = new List<byte>(entry.Data);
                    foreach (var (offset, chunk) in entry.Pending.OrderBy(p => p.Key))
                    {
                        if (offset != data.Count)
                        {
                            return Error(true, request, HttpStatusCode.BadRequest, "InvalidFlushPosition", "The uploaded data is not contiguous.");
                        }

                        data.AddRange(chunk);
                    }

                    if (data.Count != position)
                    {
                        return Error(true, request, HttpStatusCode.BadRequest, "InvalidFlushPosition", "The position is not the length of the file.");
                    }

                    entry.Pending.Clear();
                    entry.Data = [.. data];
                    entry.Md5 = request.Headers.TryGetValues("x-ms-content-md5", out var md5) ? Convert.FromBase64String(md5.Single()) : null;
                    entry.Modified = Tick();
                    entry.ETag = NewETag();
                    var response = Ok(HttpStatusCode.OK);
                    Stamp(response, entry);
                    return response;
                }

                return Error(true, request, HttpStatusCode.BadRequest, "InvalidQueryParameterValue", "Unknown action.");
            }

            case "DELETE":
                if (entry is null)
                {
                    return Error(true, request, HttpStatusCode.NotFound, "PathNotFound", "The specified path does not exist.");
                }

                files.Remove(path);
                return Ok(HttpStatusCode.OK);
        }

        return Error(true, request, HttpStatusCode.MethodNotAllowed, "UnsupportedHttpVerb", "Not served.");
    }

    private HttpResponseMessage ListPaths(HttpRequestMessage request, string container, Dictionary<string, string> query)
    {
        if (!_containers.TryGetValue(container, out var files))
        {
            return Error(true, request, HttpStatusCode.NotFound, "FilesystemNotFound", "The specified filesystem does not exist.");
        }

        var directory = query.GetValueOrDefault("directory");
        if (!string.IsNullOrEmpty(directory) && !(files.TryGetValue(directory, out var dir) && dir.IsDirectory))
        {
            return Error(true, request, HttpStatusCode.NotFound, "PathNotFound", "The specified path does not exist.");
        }

        var recursive = query["recursive"] == "true";
        var prefix = string.IsNullOrEmpty(directory) ? string.Empty : directory + "/";
        var matched = files
            .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && (recursive || !f.Key[prefix.Length..].Contains('/')))
            .ToList();
        var start = query.TryGetValue("continuation", out var token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        var page = matched.Skip(start).Take(DfsPageSize).ToList();

        // The service writes every value as a string, and omits isDirectory for a file.
        var paths = page.Select(f =>
        {
            var item = new Dictionary<string, string>
            {
                ["name"] = f.Key,
                ["contentLength"] = (f.Value.IsDirectory ? 0 : f.Value.Data.Length).ToString(CultureInfo.InvariantCulture),
                ["lastModified"] = f.Value.Modified.ToString("r", CultureInfo.InvariantCulture),
                ["etag"] = f.Value.ETag.Trim('"'),
                ["owner"] = "$superuser",
            };
            if (f.Value.IsDirectory)
            {
                item["isDirectory"] = "true";
            }

            return item;
        }).ToList();

        var response = Ok(HttpStatusCode.OK, JsonSerializer.SerializeToUtf8Bytes(new { paths }), "application/json");
        if (start + page.Count < matched.Count)
        {
            response.Headers.TryAddWithoutValidation("x-ms-continuation", (start + page.Count).ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    private HttpResponseMessage ServeBlob(HttpRequestMessage request, string container, string path, Dictionary<string, string> query, byte[] body)
    {
        var method = request.Method.Method;
        if (path.Length == 0 && query.GetValueOrDefault("restype") == "container")
        {
            if (query.GetValueOrDefault("comp") == "list")
            {
                return ListBlobs(request, container, query);
            }

            if (method is "GET" or "HEAD")
            {
                if (!_containers.ContainsKey(container))
                {
                    return Error(false, request, HttpStatusCode.NotFound, "ContainerNotFound", "The specified container does not exist.");
                }

                var response = Ok(HttpStatusCode.OK);
                response.Headers.TryAddWithoutValidation("ETag", "\"0x8DCONTAINER\"");
                response.Content.Headers.LastModified = Epoch;
                return response;
            }

            if (method == "PUT")
            {
                return _containers.TryAdd(container, new SortedDictionary<string, Entry>(StringComparer.Ordinal))
                    ? Ok(HttpStatusCode.Created)
                    : Error(false, request, HttpStatusCode.Conflict, "ContainerAlreadyExists", "The specified container already exists.");
            }
        }

        if (!_containers.TryGetValue(container, out var files))
        {
            return Error(false, request, HttpStatusCode.NotFound, "ContainerNotFound", "The specified container does not exist.");
        }

        files.TryGetValue(path, out var entry);
        switch (method)
        {
            case "HEAD":
            {
                if (entry is null)
                {
                    return Error(false, request, HttpStatusCode.NotFound, "BlobNotFound", "The specified blob does not exist.");
                }

                var response = Ok(HttpStatusCode.OK, Array.Empty<byte>());
                response.Content.Headers.ContentLength = entry.IsDirectory ? 0 : entry.Data.Length;
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                if (entry.IsDirectory)
                {
                    response.Headers.TryAddWithoutValidation("x-ms-meta-hdi_isfolder", "true");
                }

                Stamp(response, entry);
                return response;
            }

            case "GET":
                return entry is null
                    ? Error(false, request, HttpStatusCode.NotFound, "BlobNotFound", "The specified blob does not exist.")
                    : Read(request, entry, dfs: false);

            case "PUT":
            {
                if (request.Headers.TryGetValues("If-None-Match", out var none) && none.Contains("*") && entry is not null)
                {
                    return Error(false, request, HttpStatusCode.Conflict, "BlobAlreadyExists", "The specified blob already exists.");
                }

                EnsureParents(files, path);
                var md5 = request.Headers.TryGetValues("x-ms-blob-content-md5", out var hash) ? Convert.FromBase64String(hash.Single()) : null;
                var created = new Entry(false, body, md5, Tick(), NewETag());
                files[path] = created;
                var response = Ok(HttpStatusCode.Created);
                Stamp(response, created);
                return response;
            }

            case "DELETE":
                if (entry is null)
                {
                    return Error(false, request, HttpStatusCode.NotFound, "BlobNotFound", "The specified blob does not exist.");
                }

                files.Remove(path);
                return Ok(HttpStatusCode.Accepted);
        }

        return Error(false, request, HttpStatusCode.MethodNotAllowed, "UnsupportedHttpVerb", "Not served.");
    }

    private HttpResponseMessage ListBlobs(HttpRequestMessage request, string container, Dictionary<string, string> query)
    {
        if (!_containers.TryGetValue(container, out var files))
        {
            return Error(false, request, HttpStatusCode.NotFound, "ContainerNotFound", "The specified container does not exist.");
        }

        var prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        var delimiter = query.GetValueOrDefault("delimiter");
        // The SDK spells it "Metadata"; the service reads the include list without regard to case.
        var metadata = (query.GetValueOrDefault("include") ?? string.Empty).Contains("metadata", StringComparison.OrdinalIgnoreCase);
        var blobs = new XElement("Blobs");
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, entry) in files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var rest = name[prefix.Length..];
            if (delimiter is not null && rest.Contains(delimiter, StringComparison.Ordinal))
            {
                var folder = prefix + rest[..(rest.IndexOf(delimiter, StringComparison.Ordinal) + delimiter.Length)];
                if (prefixes.Add(folder))
                {
                    blobs.Add(new XElement("BlobPrefix", new XElement("Name", folder)));
                }

                continue;
            }

            var properties = new XElement("Properties",
                new XElement("Creation-Time", Epoch.ToString("r", CultureInfo.InvariantCulture)),
                new XElement("Last-Modified", entry.Modified.ToString("r", CultureInfo.InvariantCulture)),
                new XElement("Etag", entry.ETag),
                new XElement("Content-Length", entry.IsDirectory ? 0 : entry.Data.Length),
                new XElement("Content-Type", "application/octet-stream"),
                new XElement("Content-MD5", entry.Md5 is null ? string.Empty : Convert.ToBase64String(entry.Md5)),
                new XElement("BlobType", "BlockBlob"),
                new XElement("LeaseStatus", "unlocked"),
                new XElement("LeaseState", "available"),
                new XElement("ServerEncrypted", "true"));
            var blob = new XElement("Blob", new XElement("Name", name), properties);
            if (metadata)
            {
                blob.Add(entry.IsDirectory ? new XElement("Metadata", new XElement("hdi_isfolder", "true")) : new XElement("Metadata"));
            }

            blobs.Add(blob);
        }

        var document = new XDocument(new XElement("EnumerationResults",
            new XAttribute("ServiceEndpoint", $"https://{BlobHost}/"),
            new XAttribute("ContainerName", container),
            new XElement("Prefix", prefix),
            delimiter is null ? null : new XElement("Delimiter", delimiter),
            blobs,
            new XElement("NextMarker")));
        return Ok(HttpStatusCode.OK, Encoding.UTF8.GetBytes(document.Declaration + document.ToString(SaveOptions.DisableFormatting)), "application/xml");
    }

    private HttpResponseMessage Read(HttpRequestMessage request, Entry entry, bool dfs)
    {
        if (request.Headers.TryGetValues("If-Match", out var match) && match.Single() != entry.ETag)
        {
            return Error(dfs, request, HttpStatusCode.PreconditionFailed, "ConditionNotMet", "The condition specified using HTTP conditional header(s) is not met.");
        }

        var range = request.Headers.TryGetValues("x-ms-range", out var msRange) ? msRange.Single()
            : request.Headers.Range?.ToString();
        var data = entry.Data;
        long start = 0;
        var end = data.Length - 1L;
        var partial = false;
        if (!string.IsNullOrEmpty(range))
        {
            var spec = range["bytes=".Length..].Split('-');
            start = long.Parse(spec[0], CultureInfo.InvariantCulture);
            if (spec[1].Length > 0)
            {
                end = Math.Min(end, long.Parse(spec[1], CultureInfo.InvariantCulture));
            }

            if (start >= data.Length)
            {
                return Error(dfs, request, HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "The range specified is invalid for the current size of the resource.");
            }

            partial = true;
        }

        var slice = data.AsSpan((int)start, (int)(end - start + 1)).ToArray();
        HttpContent content;
        if (!partial && BreakNextReadAfter is { } after)
        {
            BreakNextReadAfter = null;
            content = new StreamContent(new BreakingStream(slice, after));
        }
        else
        {
            content = new ByteArrayContent(slice);
        }

        content.Headers.ContentLength = slice.Length;
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        if (partial)
        {
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(start, end, data.Length);
        }

        if (!dfs)
        {
            response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        }

        Stamp(response, entry);
        return response;
    }

    private static void Stamp(HttpResponseMessage response, Entry entry)
    {
        response.Headers.TryAddWithoutValidation("ETag", entry.ETag);
        response.Content ??= new ByteArrayContent([]);
        response.Content.Headers.LastModified = entry.Modified;
        if (entry.Md5 is not null)
        {
            response.Content.Headers.ContentMD5 = entry.Md5;
        }
    }

    private static HttpResponseMessage Ok(HttpStatusCode status, byte[]? body = null, string? contentType = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        if (contentType is not null)
        {
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        return response;
    }

    private static HttpResponseMessage Error(bool dfs, HttpRequestMessage request, HttpStatusCode status, string code, string message)
    {
        var body = request.Method == HttpMethod.Head
            ? []
            : dfs
                ? JsonSerializer.SerializeToUtf8Bytes(new { error = new { code, message } })
                : Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{message}</Message></Error>");
        // A HEAD error has no body, and the service then names no content type for one.
        var response = Ok(status, body, body.Length == 0 ? null : dfs ? "application/json" : "application/xml");
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        return response;
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
            result[name] = value;
        }

        return result;
    }

    private void EnsureParents(SortedDictionary<string, Entry> files, string path)
    {
        var slash = path.LastIndexOf('/');
        while (slash > 0)
        {
            var parent = path[..slash];
            files.TryAdd(parent, Directory());
            slash = parent.LastIndexOf('/');
        }
    }

    private Entry Directory() => new(true, [], null, Tick(), NewETag());

    private DateTimeOffset Tick() => Epoch.AddSeconds(Interlocked.Increment(ref _clock));

    private string NewETag() => $"\"0x8DC{Interlocked.Increment(ref _etags):X8}\"";

    private sealed class Entry(bool isDirectory, byte[] data, byte[]? md5, DateTimeOffset modified, string etag)
    {
        public bool IsDirectory { get; } = isDirectory;
        public byte[] Data { get; set; } = data;
        public byte[]? Md5 { get; set; } = md5;
        public DateTimeOffset Modified { get; set; } = modified;
        public string ETag { get; set; } = etag;
        public Dictionary<long, byte[]> Pending { get; } = [];
    }

    /// <summary>A body that delivers some bytes and then fails as a dropped connection does.</summary>
    private sealed class BreakingStream(byte[] data, int breakAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= breakAfter)
            {
                throw new IOException("The connection was reset by the test.");
            }

            var take = Math.Min(count, breakAfter - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>How an endpoint of <see cref="FakeStorageAccount"/> treats this client.</summary>
public enum EndpointBehavior
{
    Serve,
    RejectNetwork,
    Unresolvable,
    Refused,

    /// <summary>Answers every request with 500: the service received it, so it may have been processed.</summary>
    ServerError,
}

/// <summary>One request the fake account received.</summary>
internal sealed record Recorded(string Host, string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers);
