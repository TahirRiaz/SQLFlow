using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// The Data Lake Storage REST API (<c>&lt;account&gt;.dfs.core.windows.net</c>) for the operations the storage layer
/// needs, over an Azure.Core pipeline (retries, logging and authentication as the Azure SDK clients have them). The
/// Data Lake SDK cannot serve this role: its reads, property reads and existence checks go to the Blob endpoint, so on a
/// network that reaches only the Data Lake endpoint they fail. Every request here goes to the Data Lake endpoint.
/// The operations follow the service's REST contract (Azure.Storage.Files.DataLake, DataLakeStorage.json):
/// <c>FileSystem_GetProperties</c>, <c>FileSystem_Create</c>, <c>FileSystem_ListPaths</c>, <c>Path_GetProperties</c>,
/// <c>Path_Read</c>, <c>Path_Create</c>, <c>Path_AppendData</c>, <c>Path_FlushData</c> and <c>Path_Delete</c>.
/// Each operation has a synchronous form (<c>async: false</c> completes without awaiting) for the streams' synchronous
/// members, in the pattern the Azure SDK clients use.
/// </summary>
internal sealed class DfsRestClient
{
    /// <summary>The REST version the requests ask for: every operation used here exists in it.</summary>
    internal const string ServiceVersion = "2025-01-05";

    /// <summary>The token scope for Azure Storage, the same the Blob SDK requests.</summary>
    internal const string StorageScope = "https://storage.azure.com/.default";

    /// <summary>The service's own page limit for a listing.</summary>
    private const int ListPageSize = 5000;

    /// <summary>The most of an error response's body kept for its exception: error bodies are small JSON documents.</summary>
    private const int MaxErrorBodyBytes = 64 * 1024;

    private static readonly DfsErrorParser ErrorParser = new();

    private readonly Uri _endpoint;
    private readonly HttpPipeline _pipeline;

    internal DfsRestClient(Uri endpoint, HttpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(pipeline);
        _endpoint = endpoint;
        _pipeline = pipeline;
    }

    /// <summary>
    /// A pipeline for Data Lake requests. A shared access signature goes in as a per-call policy (appended once, not on
    /// every retry of the same message); a bearer token policy goes last among the per-retry policies, as the Azure SDK
    /// places it, so a retried request carries a fresh token.
    /// </summary>
    internal static HttpPipeline CreatePipeline(
        HttpPipelinePolicy[] perCall, HttpPipelinePolicy[] perRetry, HttpPipelineTransport? transport, int maxRetries,
        TimeSpan? networkTimeout, TimeSpan? retryDelay)
    {
        var options = new DfsClientOptions();
        options.Retry.MaxRetries = maxRetries;
        if (networkTimeout is { } timeout)
        {
            options.Retry.NetworkTimeout = timeout;
        }

        if (retryDelay is { } delay)
        {
            options.Retry.Delay = delay;
        }

        if (transport is not null)
        {
            options.Transport = transport;
        }

        return HttpPipelineBuilder.Build(options, perCall, perRetry, responseClassifier: null);
    }

    /// <summary>Reads the file system's properties; throws when the request fails (status 404 when it does not exist).</summary>
    internal async Task GetFileSystemPropertiesAsync(string fileSystem, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Head, fileSystem, path: null);
        message.Request.Uri.AppendQuery("resource", "filesystem");
        await SendAsync(message, async: true, ct).ConfigureAwait(false);
        if (message.Response.Status != 200)
        {
            throw await FailureAsync(message, async: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Creates the file system; false when it already exists.</summary>
    internal async Task<bool> CreateFileSystemAsync(string fileSystem, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Put, fileSystem, path: null);
        message.Request.Uri.AppendQuery("resource", "filesystem");
        message.Request.Content = RequestContent.Create(ReadOnlyMemory<byte>.Empty);
        await SendAsync(message, async: true, ct).ConfigureAwait(false);
        return message.Response.Status switch
        {
            201 => true,
            409 when ErrorCode(message.Response) == "FilesystemAlreadyExists" => false,
            _ => throw await FailureAsync(message, async: true, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// One page of the paths under <paramref name="directory"/> (the file system root when null), with the token for the
    /// next page or null on the last. A directory that does not exist lists nothing, as a Blob prefix with no blobs does.
    /// </summary>
    internal async Task<(IReadOnlyList<AzureStorageItem> Items, string? Continuation)> ListPathsAsync(
        string fileSystem, string? directory, bool recursive, string? continuation, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Get, fileSystem, path: null);
        var uri = message.Request.Uri;
        uri.AppendQuery("resource", "filesystem");
        uri.AppendQuery("recursive", recursive ? "true" : "false");
        if (!string.IsNullOrEmpty(directory))
        {
            uri.AppendQuery("directory", directory);
        }

        if (!string.IsNullOrEmpty(continuation))
        {
            uri.AppendQuery("continuation", continuation);
        }

        uri.AppendQuery("maxResults", ListPageSize.ToString(CultureInfo.InvariantCulture));
        await SendAsync(message, async: true, ct).ConfigureAwait(false);

        var response = message.Response;
        if (response.Status == 404 && directory is not null && ErrorCode(response) == "PathNotFound")
        {
            return ([], null);
        }

        if (response.Status != 200)
        {
            throw await FailureAsync(message, async: true, ct).ConfigureAwait(false);
        }

        var items = ParsePaths(response);
        var next = response.Headers.TryGetValue("x-ms-continuation", out var token) && !string.IsNullOrEmpty(token) ? token : null;
        return (items, next);
    }

    /// <summary>The path's properties, or null when the path (or its file system) does not exist.</summary>
    internal async Task<AzureStorageProperties?> GetPathPropertiesAsync(string fileSystem, string path, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Head, fileSystem, path);
        await SendAsync(message, async: true, ct).ConfigureAwait(false);
        var response = message.Response;
        if (response.Status == 404)
        {
            return null;
        }

        if (response.Status != 200)
        {
            throw await FailureAsync(message, async: true, ct).ConfigureAwait(false);
        }

        var isDirectory = response.Headers.TryGetValue("x-ms-resource-type", out var type)
            && string.Equals(type, "directory", StringComparison.OrdinalIgnoreCase);
        return new AzureStorageProperties(
            isDirectory ? 0 : response.Headers.ContentLength ?? 0,
            LastModified(response),
            isDirectory,
            ContentMd5(response),
            response.Headers.TryGetValue("ETag", out var etag) ? etag : null);
    }

    /// <summary>
    /// Reads the file, or <paramref name="length"/> bytes of it from <paramref name="offset"/> (to the end when null),
    /// returning the response's body as a stream the caller owns. With <paramref name="ifMatch"/> the read fails with
    /// status 412 when the file has changed since that entity tag.
    /// </summary>
    internal async ValueTask<DfsRead> ReadAsync(
        string fileSystem, string path, long offset, long? length, string? ifMatch, bool async, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Get, fileSystem, path);
        message.BufferResponse = false;
        if (offset > 0 || length is not null)
        {
            message.Request.Headers.SetValue(
                "Range",
                length is { } count
                    ? $"bytes={offset.ToString(CultureInfo.InvariantCulture)}-{(offset + count - 1).ToString(CultureInfo.InvariantCulture)}"
                    : $"bytes={offset.ToString(CultureInfo.InvariantCulture)}-");
        }

        if (ifMatch is not null)
        {
            message.Request.Headers.SetValue("If-Match", ifMatch);
        }

        await SendAsync(message, async, ct).ConfigureAwait(false);
        var response = message.Response;
        if (response.Status is not (200 or 206))
        {
            throw await FailureAsync(message, async, ct).ConfigureAwait(false);
        }

        var etag = response.Headers.TryGetValue("ETag", out var tag) ? tag : null;
        var bodyLength = response.Headers.ContentLength ?? -1;
        var total = response.Status == 200 ? bodyLength : TotalFromContentRange(response);
        var content = message.ExtractResponseContent() ?? new MemoryStream(Array.Empty<byte>(), writable: false);
        return new DfsRead(content, bodyLength, total, etag);
    }

    /// <summary>Creates (or, with <paramref name="overwrite"/>, replaces) an empty file; without it the request fails
    /// with status 409 when the path exists. Parent directories are created as needed.</summary>
    internal async Task CreateFileAsync(string fileSystem, string path, bool overwrite, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Put, fileSystem, path);
        message.Request.Uri.AppendQuery("resource", "file");
        if (!overwrite)
        {
            message.Request.Headers.SetValue("If-None-Match", "*");
        }

        message.Request.Content = RequestContent.Create(ReadOnlyMemory<byte>.Empty);
        await SendAsync(message, async: true, ct).ConfigureAwait(false);
        if (message.Response.Status != 201)
        {
            throw await FailureAsync(message, async: true, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Uploads <paramref name="data"/> to be appended at <paramref name="position"/>; it is part of the file only
    /// once a flush commits it. Resending the same bytes at the same position (a retry) is safe.</summary>
    internal async ValueTask AppendAsync(string fileSystem, string path, long position, ReadOnlyMemory<byte> data, bool async, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Patch, fileSystem, path);
        message.Request.Uri.AppendQuery("action", "append");
        message.Request.Uri.AppendQuery("position", position.ToString(CultureInfo.InvariantCulture));
        message.Request.Content = RequestContent.Create(data);
        await SendAsync(message, async, ct).ConfigureAwait(false);
        if (message.Response.Status != 202)
        {
            throw await FailureAsync(message, async, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commits the appended data up to <paramref name="position"/> (the file's full length). <paramref name="contentHash"/>
    /// is stored as the file's Content-MD5 (the Blob endpoint's stored content hash); without it the stored hash is cleared,
    /// as the service documents. <paramref name="close"/> marks the final flush of a write, which is when Azure Storage
    /// raises its BlobCreated event for a Data Lake write.
    /// </summary>
    internal async ValueTask FlushAsync(
        string fileSystem, string path, long position, byte[]? contentHash, bool close, bool async, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Patch, fileSystem, path);
        message.Request.Uri.AppendQuery("action", "flush");
        message.Request.Uri.AppendQuery("position", position.ToString(CultureInfo.InvariantCulture));
        if (close)
        {
            message.Request.Uri.AppendQuery("close", "true");
        }

        if (contentHash is not null)
        {
            message.Request.Headers.SetValue("x-ms-content-md5", Convert.ToBase64String(contentHash));
        }

        message.Request.Content = RequestContent.Create(ReadOnlyMemory<byte>.Empty);
        await SendAsync(message, async, ct).ConfigureAwait(false);
        if (message.Response.Status != 200)
        {
            throw await FailureAsync(message, async, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a file; false when nothing was there.</summary>
    internal async ValueTask<bool> DeletePathAsync(string fileSystem, string path, bool async, CancellationToken ct)
    {
        using var message = CreateMessage(RequestMethod.Delete, fileSystem, path);
        await SendAsync(message, async, ct).ConfigureAwait(false);
        return message.Response.Status switch
        {
            200 or 202 => true,
            404 => false,
            _ => throw await FailureAsync(message, async, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Escapes an in-container path for the request URI one segment at a time, so its separators stay
    /// separators and every other reserved or non-ASCII character is percent-encoded.</summary>
    internal static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private HttpMessage CreateMessage(RequestMethod method, string fileSystem, string? path)
    {
        var message = _pipeline.CreateMessage();
        var request = message.Request;
        request.Method = method;
        request.Uri.Reset(_endpoint);
        // AppendPath, not the Path setter: Reset caches the endpoint's Uri and only AppendPath (and AppendQuery) clear
        // that cache, so a path set any other way would be dropped from a request with no query string.
        request.Uri.AppendPath(
            path is null
                ? "/" + Uri.EscapeDataString(fileSystem)
                : "/" + Uri.EscapeDataString(fileSystem) + "/" + EscapePath(path),
            escape: false);
        request.Headers.SetValue("x-ms-version", ServiceVersion);
        return message;
    }

    private async ValueTask SendAsync(HttpMessage message, bool async, CancellationToken ct)
    {
        if (async)
        {
            await _pipeline.SendAsync(message, ct).ConfigureAwait(false);
        }
        else
        {
            _pipeline.Send(message, ct);
        }
    }

    /// <summary>
    /// The exception for a failed response, carrying the service's error code and message. A streamed response's body is
    /// buffered first (bounded: an error body is a small JSON document), because the exception reads it.
    /// </summary>
    private static async ValueTask<RequestFailedException> FailureAsync(HttpMessage message, bool async, CancellationToken ct)
    {
        var response = message.Response;
        if (response.ContentStream is { } body and not MemoryStream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            using (body)
            {
                while (buffer.Length < MaxErrorBodyBytes)
                {
                    var read = async
                        ? await body.ReadAsync(chunk, ct).ConfigureAwait(false)
                        : body.Read(chunk, 0, chunk.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            buffer.Position = 0;
            response.ContentStream = buffer;
        }

        return new RequestFailedException(response, null, ErrorParser);
    }

    private static string? ErrorCode(Response response)
        => response.Headers.TryGetValue("x-ms-error-code", out var code) ? code : null;

    private static List<AzureStorageItem> ParsePaths(Response response)
    {
        var items = new List<AzureStorageItem>();
        var content = response.Content;
        if (content.ToMemory().IsEmpty)
        {
            return items;
        }

        using var document = JsonDocument.Parse(content.ToMemory());
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(document.RootElement, "paths", out var paths)
            || paths.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var path in paths.EnumerateArray())
        {
            var name = StringOf(path, "name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var isDirectory = BooleanOf(path, "isDirectory");
            items.Add(new AzureStorageItem(
                name.TrimEnd('/'),
                isDirectory,
                isDirectory ? 0 : Int64Of(path, "contentLength"),
                DateOf(StringOf(path, "lastModified")),
                ContentHash: null,
                ContentHashListed: false));
        }

        return items;
    }

    // The service writes a path's values as JSON strings ("contentLength": "12", "isDirectory": "true") although its
    // contract types them as a number and a boolean; both spellings are read.
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? StringOf(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BooleanOf(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static long Int64Of(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static DateTimeOffset? DateOf(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rfc1123))
        {
            return rfc1123;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? LastModified(Response response)
        => response.Headers.TryGetValue("Last-Modified", out var value) ? DateOf(value) : null;

    private static byte[]? ContentMd5(Response response)
    {
        if (!response.Headers.TryGetValue("Content-MD5", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var bytes = new byte[(value.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(value.Trim(), bytes, out var written) && written > 0 ? bytes[..written] : null;
    }

    /// <summary>The file's full length from a range response's <c>Content-Range: bytes a-b/total</c>, or -1.</summary>
    private static long TotalFromContentRange(Response response)
    {
        if (!response.Headers.TryGetValue("Content-Range", out var range))
        {
            return -1;
        }

        var slash = range.LastIndexOf('/');
        return slash >= 0 && long.TryParse(range.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            ? total
            : -1;
    }
}

/// <summary>A read's body (owned by the caller), its length, the file's full length (-1 when unknown) and its entity tag.</summary>
internal readonly record struct DfsRead(Stream Content, long Length, long TotalLength, string? ETag);

/// <summary>The Data Lake client's options: the Azure Storage clients' five retries, and the storage headers and query
/// parameters that are safe to log (never <c>sig</c>, so a shared access signature stays redacted).</summary>
internal sealed class DfsClientOptions : ClientOptions
{
    public DfsClientOptions()
    {
        Retry.MaxRetries = 5;
        foreach (var header in new[] { "x-ms-error-code", "x-ms-version", "x-ms-resource-type", "Content-Range", "Accept-Ranges" })
        {
            Diagnostics.LoggedHeaderNames.Add(header);
        }

        foreach (var parameter in new[] { "resource", "recursive", "action", "position", "close", "maxResults" })
        {
            Diagnostics.LoggedQueryParameters.Add(parameter);
        }
    }
}

/// <summary>
/// Reads a Data Lake error: the code from the <c>x-ms-error-code</c> header (present on every error, HEAD included) or the
/// JSON body's <c>error.code</c>, and the message from the body's <c>error.message</c>.
/// </summary>
internal sealed class DfsErrorParser : RequestFailedDetailsParser
{
    public override bool TryParse(Response response, out ResponseError? error, out IDictionary<string, string>? data)
    {
        data = null;
        response.Headers.TryGetValue("x-ms-error-code", out var code);
        string? bodyCode = null;
        string? message = null;
        if (response.ContentStream is MemoryStream && !response.Content.ToMemory().IsEmpty)
        {
            try
            {
                using var document = JsonDocument.Parse(response.Content.ToMemory());
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var field in property.Value.EnumerateObject())
                        {
                            if (field.Value.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }

                            if (string.Equals(field.Name, "code", StringComparison.OrdinalIgnoreCase))
                            {
                                bodyCode = field.Value.GetString();
                            }
                            else if (string.Equals(field.Name, "message", StringComparison.OrdinalIgnoreCase))
                            {
                                message = field.Value.GetString();
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A body that is not the service's JSON error (a proxy's HTML page) carries no code or message of its own;
                // the header's code and the response's status still describe the failure.
                message = null;
            }
        }

        error = new ResponseError(code ?? bodyCode, message);
        return true;
    }
}
