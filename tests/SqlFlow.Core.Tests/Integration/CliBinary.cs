using System.Diagnostics;
using System.Text;

namespace SqlFlow.Tests.Integration;

/// <summary>One finished CLI invocation: the exit code and both streams, fully drained.</summary>
public sealed record CliResult(int Exit, string StdOut, string StdErr)
{
    /// <summary>Both streams joined, for assertion messages that need everything the process said.</summary>
    public string AllOutput => StdOut + (StdErr.Length > 0 ? "\n--- stderr ---\n" + StdErr : string.Empty);
}

/// <summary>
/// The shared harness for tests that exercise the actual compiled CLI binary (<c>sqlflow.dll</c>) as a child
/// process: real argument parsing, the real DI composition root, real console streams, and real exit codes.
/// Isolation is deliberate: the working directory defaults to a location OUTSIDE the repository so the
/// repo's own <c>.sqlflow/env</c> (which points at the developer's live catalog) is never picked up, and
/// <c>SQLFLOW_CATALOG_DB</c> / <c>SQLFLOW_URL</c> / <c>SQLFLOW_TOKEN</c> / <c>SQLFLOW_CREDENTIALS_FILE</c>
/// are cleared unless a test passes them explicitly, so a developer's shell environment cannot leak into an
/// assertion.
/// </summary>
public static class CliBinary
{
    /// <summary>The environment variables the harness clears by default so the host machine's configuration
    /// never reaches the child process implicitly.</summary>
    private static readonly string[] IsolatedVariables =
    [
        "SQLFLOW_CATALOG_DB", "SQLFLOW_URL", "SQLFLOW_TOKEN", "SQLFLOW_CREDENTIALS_FILE", "SQLFLOW_REPO",
    ];

    /// <summary>Locates the built CLI (Release preferred, Debug fallback) by walking up from the test bin.</summary>
    public static string? DllPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var projectBin = Path.Combine(dir.FullName, "src", "SqlFlow.Cli", "bin");
            if (Directory.Exists(projectBin))
            {
                foreach (var config in new[] { "Release", "Debug" })
                {
                    var candidate = Path.Combine(projectBin, config, "net9.0", "sqlflow.dll");
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

    /// <summary>Runs the CLI to completion. <paramref name="stdin"/>, when non-null, is piped to the process
    /// (how the CLI reads secrets in automation); <paramref name="workingDirectory"/> defaults to the system
    /// temp directory (outside the repo, so no <c>.sqlflow/env</c> is found).</summary>
    public static async Task<CliResult> RunAsync(
        string dll, string[] args, (string Name, string? Value)[]? env = null, string? stdin = null,
        string? workingDirectory = null, int timeoutSeconds = 180)
    {
        await using var process = Start(dll, args, env, redirectStdIn: stdin is not null, workingDirectory);
        if (stdin is not null)
        {
            await process.Process.StandardInput.WriteAsync(stdin);
            process.Process.StandardInput.Close();
        }

        var exit = await process.WaitForExitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        return new CliResult(exit, process.StdOut, process.StdErr);
    }

    /// <summary>Starts the CLI without waiting, for verbs that run until stopped (worker) or that need their
    /// output observed mid-flight (the device sign-in's user code). Dispose kills the process if it is still
    /// alive, so a failed assertion never leaks a child.</summary>
    public static CliProcess Start(
        string dll, string[] args, (string Name, string? Value)[]? env = null, bool redirectStdIn = false,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdIn,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var name in IsolatedVariables)
        {
            psi.Environment.Remove(name);
        }

        foreach (var (name, value) in env ?? [])
        {
            if (value is null)
            {
                psi.Environment.Remove(name);
            }
            else
            {
                psi.Environment[name] = value;
            }
        }

        return new CliProcess(Process.Start(psi)!);
    }
}

/// <summary>A running CLI child process with incrementally captured streams.</summary>
public sealed class CliProcess : IAsyncDisposable
{
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();

    internal CliProcess(Process process)
    {
        Process = process;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_gate)
                {
                    _stdout.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_gate)
                {
                    _stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public Process Process { get; }

    public string StdOut
    {
        get
        {
            lock (_gate)
            {
                return _stdout.ToString();
            }
        }
    }

    public string StdErr
    {
        get
        {
            lock (_gate)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>Polls the captured stdout until <paramref name="predicate"/> matches a line, returning that
    /// line, or null when the timeout elapses or the process exits without ever matching.</summary>
    public async Task<string?> WaitForOutputLineAsync(Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = StdOut.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(predicate);
            if (line is not null)
            {
                return line;
            }

            if (Process.HasExited)
            {
                return StdOut.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(predicate);
            }

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>Waits for a clean exit, killing the process (and failing the wait) on timeout so a hung verb
    /// surfaces as a test failure rather than a stuck build.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"The CLI did not exit within {timeout.TotalSeconds:0}s.\nstdout:\n{StdOut}\nstderr:\n{StdErr}");
        }

        // The async stream readers can still be draining when WaitForExitAsync returns; the parameterless
        // WaitForExit flushes them so StdOut/StdErr are complete.
        Process.WaitForExit();
        return Process.ExitCode;
    }

    public ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
        }

        Process.Dispose();
        return ValueTask.CompletedTask;
    }
}
