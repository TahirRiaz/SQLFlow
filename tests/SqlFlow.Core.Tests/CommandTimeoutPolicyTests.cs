using System.Globalization;
using System.Text;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// Guards the engine's client-timeout policy at the source level: every database command on a data-plane
/// path must state its <c>CommandTimeout</c> explicitly.
/// <para>
/// ADO.NET defaults <c>CommandTimeout</c> to 30 seconds. Engine work does not fit in 30 seconds and is not
/// supposed to: a catalog introspection queues behind concurrent DDL, a TRUNCATE waits for a Sch-M lock, an
/// index build scans a freshly loaded table, an app-lock acquisition deliberately waits out its own
/// <c>@LockTimeout</c>. Inheriting the default turns each of those normal waits into a hard
/// "Execution Timeout Expired" that kills the flow, which is exactly the failure mode this policy exists to
/// prevent. Commands are therefore either unbounded (<c>CommandTimeout = 0</c>, the server decides) or carry
/// a deliberate, documented bound (see <c>AssertionRunner</c>). What is forbidden is saying nothing and
/// silently getting 30.
/// </para>
/// This is a source scan rather than a runtime check because the defect is an omission: there is no object
/// to inspect for a command that was never written correctly.
/// </summary>
public sealed class CommandTimeoutPolicyTests
{
    /// <summary>Assemblies whose commands run against a user's data or catalog, where a wait is normal.</summary>
    private static readonly string[] ScannedProjects =
    [
        "SqlFlow.SqlServer",
        "SqlFlow.Providers",
        "SqlFlow.Lineage",
    ];

    /// <summary>How far after the construction site the assignment may appear. Wide enough for a multi-line
    /// constructor or an object-initializer block, narrow enough that it cannot match an unrelated command.</summary>
    private const int AssignmentWindowLines = 8;

    [Fact]
    public void Every_data_plane_command_states_its_timeout()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var project in ScannedProjects)
        {
            var directory = Path.Combine(root, "src", project);
            Assert.True(Directory.Exists(directory), $"Scanned project directory is missing: {directory}");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file, directory))
                {
                    continue;
                }

                scanned++;
                offenders.AddRange(Offenders(file, root));
            }
        }

        Assert.True(scanned > 0, "The scan found no source files, so it proves nothing.");

        if (offenders.Count > 0)
        {
            var message = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"{offenders.Count} command(s) inherit ADO.NET's 30 second default CommandTimeout.")
                .AppendLine("Set CommandTimeout = 0 (let the server bound the wait), or a deliberate value with a comment saying why:")
                .AppendLine();

            foreach (var offender in offenders)
            {
                message.AppendLine(offender);
            }

            Assert.Fail(message.ToString());
        }
    }

    private static IEnumerable<string> Offenders(string file, string root)
    {
        var lines = File.ReadAllLines(file);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsConstructionSite(lines[i]))
            {
                continue;
            }

            var last = Math.Min(lines.Length - 1, i + AssignmentWindowLines);
            var stated = false;
            for (var j = i; j <= last && !stated; j++)
            {
                stated = lines[j].Contains("CommandTimeout", StringComparison.Ordinal);
            }

            if (!stated)
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                yield return string.Create(CultureInfo.InvariantCulture, $"  {relative}:{i + 1}: {lines[i].Trim()}");
            }
        }
    }

    /// <summary>A line that produces a command object. <c>.CreateCommand()</c> requires the empty argument
    /// list so it cannot match a helper named <c>CreateCommand(connection)</c> that sets the timeout itself.</summary>
    private static bool IsConstructionSite(string line)
        => line.Contains("new SqlCommand(", StringComparison.Ordinal)
        || line.Contains(".CreateCommand()", StringComparison.Ordinal);

    private static bool IsBuildOutput(string file, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Walks up from the test binary to the directory holding the solution. The scan reads sources,
    /// so a missing root is a broken test rather than a silent pass.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SqlFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate SqlFlow.sln above '{AppContext.BaseDirectory}'; the source scan cannot run.");
    }
}
