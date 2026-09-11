using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Hosts the REAL control plane binary (Kestrel, a real port) as a child process for the CLI end-to-end suite:
/// the in-memory <see cref="ControlPlaneAppFactory"/> cannot be reached by another process, and the point of
/// the CLI suite is the whole chain (real HTTP, real auth headers, real SSE) exactly as an operator runs it.
/// Started lazily on first use so suites without the catalog database skip before any process spawns; one
/// instance serves the whole collection. Configuration mirrors the in-memory factory's deterministic test
/// settings, delivered through the standard ASPNETCORE/ControlPlane__ environment keys.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate is disposed in IAsyncLifetime.DisposeAsync, which xUnit invokes for this fixture.")]
public sealed class ControlPlaneProcessFixture : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    /// <summary>The base URL the child listens on (http://127.0.0.1:port), set once started.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>The catalog connection the child was pointed at.</summary>
    public string CatalogConnection { get; private set; } = string.Empty;

    /// <summary>Starts the control plane once (idempotent) and returns its base URL. Callers gate on
    /// <see cref="CatalogTestDb.Require"/> and the built binaries BEFORE calling, so this never skips.</summary>
    public async Task<string> EnsureStartedAsync(string catalogConnection)
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is { HasExited: false })
            {
                return BaseUrl;
            }

            if (_process is { HasExited: true } dead)
            {
                // A child that died between tests is restarted, but never silently: the exit and the log of the
                // dead incarnation are kept so a test that fails against the replacement can explain itself.
                LastExit = $"the previous control plane child (pid {dead.Id}) exited with code {dead.ExitCode}; its output is in {LogPath}";
                CloseLog();
            }

            var dll = ControlPlaneDllPath();
            Assert.True(dll is not null, "Built control plane not found; run 'dotnet build -c Release' first.");
            await Catalog.CatalogDatabase.MigrateAsync(catalogConnection);

            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            CatalogConnection = catalogConnection;

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(dll)!,
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(dll!);
            psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            psi.Environment["ControlPlane__Catalog__ConnectionReference"] = catalogConnection;
            psi.Environment["ControlPlane__Jwt__SigningKey"] = ControlPlaneAppFactory.SigningKey;
            psi.Environment["ControlPlane__Jwt__BootstrapSecret"] = ControlPlaneAppFactory.BootstrapSecret;
            psi.Environment["ControlPlane__Jwt__Issuer"] = ControlPlaneAppFactory.Issuer;
            psi.Environment["ControlPlane__Jwt__Audience"] = ControlPlaneAppFactory.Audience;
            psi.Environment["ControlPlane__RateLimit__PermitPerWindow"] = "1000000";
            psi.Environment["ControlPlane__RateLimit__WindowSeconds"] = "60";
            psi.Environment["ControlPlane__Scheduler__PollSeconds"] = "1";
            psi.Environment["ControlPlane__ManagedSync__PollSeconds"] = "1";
            _process = Process.Start(psi)!;
            // Drain the streams so the child never blocks on a full pipe, and keep every line in a per-incarnation
            // log file: a child that dies mid-suite is otherwise invisible (the next test simply cannot connect).
            LogPath = Path.Combine(Path.GetTempPath(), $"sqlflow-controlplane-child-{_process.Id}.log");
            _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
            _process.OutputDataReceived += (_, e) => Record("out", e.Data);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _startupErrors.Enqueue(e.Data);
                }

                Record("err", e.Data);
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            await WaitForLiveAsync();
            return BaseUrl;
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _startupErrors = new();
    private readonly Lock _logGate = new();
    private StreamWriter? _log;

    /// <summary>Where the current child's stdout and stderr are being written, line by line.</summary>
    public string LogPath { get; private set; } = string.Empty;

    /// <summary>Set when a child was found dead and replaced: which pid exited with what code, and where its log
    /// is. A test that cannot reach the control plane should quote this.</summary>
    public string? LastExit { get; private set; }

    private void Record(string stream, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_logGate)
        {
            _log?.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] {stream}: {line}");
        }
    }

    private void CloseLog()
    {
        lock (_logGate)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    private async Task WaitForLiveAsync()
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited)
            {
                break;
            }

            try
            {
                using var response = await http.GetAsync(new Uri(BaseUrl + "/health/live"));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            $"The control plane child did not become live on {BaseUrl} within 60s. stderr:\n{string.Join('\n', _startupErrors)}");
    }

    private static string? ControlPlaneDllPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var projectBin = Path.Combine(dir.FullName, "src", "SqlFlow.ControlPlane", "bin");
            if (Directory.Exists(projectBin))
            {
                foreach (var config in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(projectBin, config, "net9.0", "SqlFlow.ControlPlane.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
        }

        _process?.Dispose();
        CloseLog();
        _gate.Dispose();

        // A killed child never releases the dispatch ownership lease it held, so the next in-memory host in the
        // run would wait out the lease's TTL before it could dispatch. Expire the lease here, as the child would
        // have done on a graceful stop, so the suite does not pay a crash's recovery time after every collection.
        if (CatalogConnection.Length > 0)
        {
            await using var catalog = Catalog.CatalogDatabase.Create(CatalogConnection);
            await catalog.DispatchLeases
                .Where(l => l.Name == Catalog.DispatchLeaseStore.DispatchLeaseName)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresUtc, DateTime.UtcNow.AddSeconds(-1)));
        }
    }
}

/// <summary>One shared control-plane child process across the CLI end-to-end classes.</summary>
[CollectionDefinition("cli-control-plane")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit's [CollectionDefinition] class is conventionally named with the 'Collection' suffix.")]
public sealed class CliControlPlaneCollection : ICollectionFixture<ControlPlaneProcessFixture>;
