using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The control plane reads the same local development file the CLI reads. A host and the documents it runs live in one
/// checkout, so the nearest <c>.sqlflow/env</c> at the content root or any parent above it is this deployment's, and the
/// in-process node resolves a flow's <c>${env:...}</c> references against this very process.
/// </summary>
[Collection("LocalEnvFile")]
public sealed class ControlPlaneLocalEnvFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-cp-env-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _touched = [];

    private string Write(string relativeDirectory, params string[] lines)
    {
        var directory = Path.Combine(_root, relativeDirectory, ".sqlflow");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "env"), lines);
        return Path.Combine(_root, relativeDirectory);
    }

    private void Remember(params string[] names) => _touched.AddRange(names);

    [Fact]
    public void A_file_in_a_parent_of_the_content_root_is_found()
    {
        // The host runs out of bin/<configuration>/<framework>, several levels under the checkout that holds the file.
        Write(string.Empty, "SQLFLOW_TEST_FROM_PARENT=applied");
        var contentRoot = Path.Combine(_root, "src", "host", "bin", "Release", "net9.0");
        Directory.CreateDirectory(contentRoot);
        Remember("SQLFLOW_TEST_FROM_PARENT");

        var applied = ControlPlaneHost.ApplyLocalEnvFile(contentRoot);

        Assert.Contains("SQLFLOW_TEST_FROM_PARENT", applied);
        Assert.Equal("applied", Environment.GetEnvironmentVariable("SQLFLOW_TEST_FROM_PARENT"));
    }

    [Fact]
    public void The_process_environment_wins_over_the_file()
    {
        // A container's or CI's variable is never shadowed by a file that happens to sit in the image.
        Remember("SQLFLOW_TEST_PROCESS_WINS");
        Environment.SetEnvironmentVariable("SQLFLOW_TEST_PROCESS_WINS", "from-the-process");
        var contentRoot = Write("host", "SQLFLOW_TEST_PROCESS_WINS=from-the-file");

        ControlPlaneHost.ApplyLocalEnvFile(contentRoot);

        Assert.Equal("from-the-process", Environment.GetEnvironmentVariable("SQLFLOW_TEST_PROCESS_WINS"));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    public void A_host_that_opted_out_reads_nothing(string opt)
    {
        // A test estate opts out so it never picks up a developer's real credentials from the checkout it runs in.
        var contentRoot = Write("host", "SQLFLOW_TEST_OPTED_OUT=applied");
        Remember("SQLFLOW_TEST_OPTED_OUT", ControlPlaneHost.LocalEnvFileVariable);
        Environment.SetEnvironmentVariable(ControlPlaneHost.LocalEnvFileVariable, opt);

        Assert.Empty(ControlPlaneHost.ApplyLocalEnvFile(contentRoot));
        Assert.Null(Environment.GetEnvironmentVariable("SQLFLOW_TEST_OPTED_OUT"));
    }

    [Fact]
    public void No_file_anywhere_above_is_not_a_failure()
    {
        var contentRoot = Path.Combine(_root, "alone");
        Directory.CreateDirectory(contentRoot);
        Assert.Empty(ControlPlaneHost.ApplyLocalEnvFile(contentRoot));
    }

    [Fact]
    public void A_malformed_file_stops_the_host_and_names_the_line()
    {
        // A half-loaded secrets file is a debugging trap: the host would start with some values and not others, and a flow
        // would fail later on a reference that looks like it should have resolved.
        var contentRoot = Write("host", "GOOD=value", "this line has no equals sign");

        var refused = Assert.Throws<SqlFlowException>(() => ControlPlaneHost.ApplyLocalEnvFile(contentRoot));

        Assert.Contains("(2)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("KEY=VALUE", refused.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var name in _touched)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory a virus scanner still holds is not this test's problem.
        }
    }
}

/// <summary>These tests mutate the process environment, so they never run beside each other.</summary>
[CollectionDefinition("LocalEnvFile", DisableParallelization = true)]
public sealed class LocalEnvFileTestGroup;
