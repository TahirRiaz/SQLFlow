using SqlFlow.Cli.Remote;

namespace SqlFlow.Cli.Hosting;

/// <summary>
/// A module verb's client for the control plane's HTTP API: the transport SQLFlow's own remote verbs use (bearer credential,
/// per-request timeout, JSON in the web defaults: camelCase and case-insensitive), for the module's own endpoints. A
/// non-success answer is thrown as a <see cref="Core.SqlFlowException"/> carrying the status and the server's problem title,
/// detail and correlation id, which the server has already redacted of secrets. Paths are relative to the control plane's
/// root (<c>/api/v1/...</c>); an absolute URL is refused, so the credential is only ever sent to the configured control plane.
/// </summary>
public sealed class CliControlPlaneClient : IDisposable
{
    private readonly ControlPlaneClient _client;

    internal CliControlPlaneClient(ControlPlaneClient client, Uri baseUrl)
    {
        _client = client;
        BaseUrl = baseUrl;
    }

    /// <summary>The control plane this client talks to.</summary>
    public Uri BaseUrl { get; }

    /// <summary>GETs <paramref name="path"/> and reads the JSON answer.</summary>
    public Task<T> GetAsync<T>(string path, CancellationToken ct) => _client.GetAsync<T>(RequirePath(path), ct);

    /// <summary>GETs <paramref name="path"/> and reads the JSON answer, or returns null when the control plane answers 404.</summary>
    public Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct)
        where T : class
        => _client.GetOrNullAsync<T>(RequirePath(path), ct);

    /// <summary>POSTs <paramref name="body"/> as JSON to <paramref name="path"/> and reads the JSON answer.</summary>
    public Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _client.PostAsync<T>(RequirePath(path), body, ct);
    }

    /// <summary>PUTs <paramref name="body"/> as JSON to <paramref name="path"/> and reads the JSON answer.</summary>
    public Task<T> PutAsync<T>(string path, object body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return _client.PutAsync<T>(RequirePath(path), body, ct);
    }

    /// <summary>DELETEs <paramref name="path"/>: true when the control plane removed it, false when it answers 404.</summary>
    public Task<bool> DeleteAsync(string path, CancellationToken ct) => _client.DeleteAsync(RequirePath(path), ct);

    public void Dispose() => _client.Dispose();

    private static string RequirePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var rooted = path[0] == '/' && (path.Length == 1 || (path[1] != '/' && path[1] != '\\'));
        if (!rooted || !Uri.TryCreate(path, UriKind.Relative, out _))
        {
            throw new ArgumentException(
                $"'{path}' is not a path on the control plane: give a path relative to its root, starting with a single '/' (for example /api/v1/runs).",
                nameof(path));
        }

        return path;
    }
}
