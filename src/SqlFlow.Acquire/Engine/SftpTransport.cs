using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// The SFTP transport: connects with a password or a private key (both resolved from secret references), lists a
/// remote directory, keeps the files matching the glob and the modified-within window, and lands each file's bytes
/// verbatim. Options (in <c>source.options</c>): <c>username</c>, <c>password</c> or <c>privateKey</c>(+<c>passphrase</c>),
/// <c>remotePath</c>, <c>pattern</c> (glob, default <c>*</c>), <c>modifiedWithinDays</c>, <c>take</c> (most-recent N).
/// The host and port come from the <c>sftp://host[:port]</c> base url.
/// </summary>
public sealed class SftpTransport : IAcquireTransport
{
    private readonly TimeProvider _time;

    public SftpTransport(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    public bool CanHandle(AcquireTransport transport) => transport == AcquireTransport.Sftp;

    public async Task FetchAsync(AcquireFetch fetch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        var options = fetch.Source.Options;
        var uri = new Uri(fetch.Source.BaseUrl, UriKind.Absolute);
        var port = uri.Port > 0 ? uri.Port : 22;
        var username = await ResolveRequired(fetch, options, "username", ct).ConfigureAwait(false);
        var remotePath = options.GetString("remotePath", ".");
        var pattern = options.GetString("pattern", "*");
        var take = options.GetInt("take", 0);
        var modifiedWithinDays = options.GetInt("modifiedWithinDays", 0);
        var cutoff = modifiedWithinDays > 0 ? _time.GetUtcNow().AddDays(-modifiedWithinDays) : (DateTimeOffset?)null;

        using var client = await BuildClientAsync(fetch, uri.Host, port, username, ct).ConfigureAwait(false);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            var files = new List<ISftpFile>();
            foreach (var entry in client.ListDirectory(remotePath))
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.IsRegularFile || !FileSystemName.MatchesSimpleExpression(pattern, entry.Name))
                {
                    continue;
                }

                if (cutoff is { } c && new DateTimeOffset(entry.LastWriteTimeUtc, TimeSpan.Zero) < c)
                {
                    continue;
                }

                files.Add(entry);
            }

            IEnumerable<ISftpFile> ordered = files.OrderBy(f => f.Name, StringComparer.Ordinal);
            if (take > 0)
            {
                ordered = files.OrderByDescending(f => f.LastWriteTimeUtc).Take(take);
            }

            var page = 0;
            foreach (var file in ordered)
            {
                ct.ThrowIfCancellationRequested();
                using var buffer = new MemoryStream();
                var startTimestamp = Stopwatch.GetTimestamp();
                client.DownloadFile(file.FullName, buffer);
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                fetch.Pages++;
                var bytes = buffer.ToArray();
                var contentType = ContentTypeFor(file.Name);
                var landed = await fetch.Landing.LandAsync(
                    new LandedItem(bytes, contentType, file.Name, RecordCount: -1, Headers: null),
                    fetch.Vars.Clone().WithString("filename", file.Name), ct).ConfigureAwait(false);
                CaptureProbe(fetch, page++, $"sftp://{uri.Host}:{port}{file.FullName}", remotePath, pattern, file, contentType, bytes, elapsed, landed?.Location);
            }

            fetch.Log.Log(RunLogLevel.Info, "sftp", $"listed {files.Count} matching file(s) under '{remotePath}'.");
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>Records one downloaded file as a debugger page: the listing selection as the "request", the file's
    /// server metadata as the "response", and a bounded preview of the bytes. Only the Test invoke passes a probe.</summary>
    private static void CaptureProbe(
        AcquireFetch fetch, int page, string url, string remotePath, string pattern, ISftpFile file,
        string? contentType, byte[] bytes, TimeSpan elapsed, string? landedTo)
    {
        if (fetch.Probe is null)
        {
            return;
        }

        fetch.Probe.Page(new AcquirePageProbe
        {
            Iteration = fetch.Iteration,
            Page = page,
            Method = "SFTP GET",
            Url = url,
            RequestHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remotePath"] = remotePath,
                ["pattern"] = pattern,
            },
            Status = 200,
            ResponseHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["file"] = file.Name,
                ["size"] = file.Length.ToString(CultureInfo.InvariantCulture),
                ["last-modified"] = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToString("o"),
            },
            ContentType = contentType,
            Bytes = bytes.Length,
            RecordCount = -1,
            DurationMs = Math.Round(elapsed.TotalMilliseconds, 1),
            BodyPreview = TransportProbe.Preview(bytes),
            LandedTo = landedTo,
        });
    }

    private static async Task<SftpClient> BuildClientAsync(AcquireFetch fetch, string host, int port, string username, CancellationToken ct)
    {
        var options = fetch.Source.Options;
        if (options.TryGetValue("privateKey", out var keyRef) && !string.IsNullOrWhiteSpace(keyRef))
        {
            var pem = await fetch.Secrets.ResolveAsync(keyRef!, ct).ConfigureAwait(false);
            var passphrase = options.TryGetValue("passphrase", out var passRef) && !string.IsNullOrWhiteSpace(passRef)
                ? await fetch.Secrets.ResolveAsync(passRef!, ct).ConfigureAwait(false)
                : null;
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(pem));
            var keyFile = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, passphrase);
            return new SftpClient(new ConnectionInfo(host, port, username, new PrivateKeyAuthenticationMethod(username, keyFile)));
        }

        if (options.TryGetValue("password", out var pwdRef) && !string.IsNullOrWhiteSpace(pwdRef))
        {
            var password = await fetch.Secrets.ResolveAsync(pwdRef!, ct).ConfigureAwait(false);
            return new SftpClient(host, port, username, password);
        }

        throw new SqlFlowException("An SFTP source requires either 'password' or 'privateKey' in source.options.");
    }

    private static async Task<string> ResolveRequired(AcquireFetch fetch, IReadOnlyDictionary<string, string?> options, string key, CancellationToken ct)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new SqlFlowException($"An SFTP source requires '{key}' in source.options.");
        }

        return await fetch.Secrets.ResolveAsync(value!, ct).ConfigureAwait(false);
    }

    private static string? ContentTypeFor(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => null,
        };
    }
}
