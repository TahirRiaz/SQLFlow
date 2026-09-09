using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

public sealed class RunHistoryWriterTests
{
    [Fact]
    public void Write_CreatesRunFolder_WithArtifacts()
    {
        var anchor = TempDir();
        try
        {
            var runId = Guid.NewGuid();
            var dir = RunHistoryWriter.Write(anchor, "orders", runId, new DateTime(2026, 6, 10, 12, 30, 5, DateTimeKind.Utc),
                new Dictionary<string, string> { ["run.json"] = "{}", ["trace.sql"] = "SELECT 1;" });

            Assert.StartsWith(Path.Combine(anchor, ".sqlflow", "runs", "orders"), dir, StringComparison.Ordinal);
            Assert.Contains("20260610-123005_", dir, StringComparison.Ordinal);
            Assert.Equal("{}", File.ReadAllText(Path.Combine(dir, "run.json")));
            Assert.Equal("SELECT 1;", File.ReadAllText(Path.Combine(dir, "trace.sql")));
        }
        finally
        {
            Cleanup(anchor);
        }
    }

    [Fact]
    public void Write_SanitizesFlowName()
    {
        var anchor = TempDir();
        try
        {
            var dir = RunHistoryWriter.Write(anchor, "bad/name:here", Guid.NewGuid(), DateTime.UtcNow,
                new Dictionary<string, string> { ["run.json"] = "{}" });
            Assert.Contains("bad_name_here", dir, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(anchor);
        }
    }

    [Fact]
    public void Write_PrunesBeyondKeepRuns()
    {
        var anchor = TempDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 0; i < RunHistoryWriter.KeepRuns + 5; i++)
            {
                RunHistoryWriter.Write(anchor, "f", Guid.NewGuid(), start.AddMinutes(i),
                    new Dictionary<string, string> { ["run.json"] = "{}" });
            }

            var flowDir = Path.Combine(anchor, ".sqlflow", "runs", "f");
            var remaining = Directory.GetDirectories(flowDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(RunHistoryWriter.KeepRuns, remaining.Count);

            // The oldest were pruned: the first remaining folder is minute 5, not minute 0.
            Assert.StartsWith("20260101-000500_", remaining[0], StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(anchor);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sfrh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
