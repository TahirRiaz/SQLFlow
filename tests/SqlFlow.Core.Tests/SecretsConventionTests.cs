using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The canonical secrets contract, end to end at the unit level: a bare connection alias resolves its
/// well-known SQLFLOW_CONN_* variable (one rule, every document kind, every platform), the .sqlflow/env
/// local-development file parses strictly and never outranks the process environment, and the secret-hygiene
/// guard recognizes embedded credentials without false-flagging references or passwordless strings.
/// </summary>
public sealed class SecretsConventionTests
{
    private static readonly YamlHealthCheckFlowLoader HealthChecks = new();
    private static readonly YamlStoredProcedureFlowLoader StoredProcedures = new();

    [Fact]
    public void BareAlias_ResolvesTheCanonicalVariable()
    {
        var doc = HealthChecks.Parse("""
            flowType: hc
            name: orders-watch
            connections:
              dwh:
            target:
              server: dwh
              object: DW.dbo.Orders
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("dwh", connection.Alias);
        Assert.Equal("${env:SQLFLOW_CONN_DWH}", connection.ConnectionRef);
        Assert.Equal(DataSourceKind.MSSQL, connection.Kind);
    }

    [Fact]
    public void BareAlias_SanitizesTheVariableName()
    {
        var doc = StoredProcedures.Parse("""
            flowType: sp
            name: refresh
            connections:
              my-dwh.prod:
            procedure:
              server: my-dwh.prod
              object: DW.dbo.usp_Refresh
            """);

        Assert.Equal("${env:SQLFLOW_CONN_MY_DWH_PROD}", Assert.Single(doc.Connections).ConnectionRef);
    }

    [Fact]
    public void MapForm_WithoutConnection_KeepsTheProviderAndTakesTheConvention()
    {
        var doc = HealthChecks.Parse("""
            flowType: hc
            name: shop-watch
            connections:
              shop:
                provider: azdb
              dwh:
            target:
              server: shop
              object: Shop.dbo.Orders
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);

        var shop = doc.Connections.Single(c => c.Alias == "shop");
        Assert.Equal("${env:SQLFLOW_CONN_SHOP}", shop.ConnectionRef);
        Assert.Equal(DataSourceKind.AZDB, shop.Kind);
    }

    [Fact]
    public void ExplicitReference_StillWinsOverTheConvention()
    {
        var doc = HealthChecks.Parse("""
            flowType: hc
            name: explicit
            connections:
              dwh: ${env:SQLFLOW_DW}
            target:
              server: dwh
              object: DW.dbo.Orders
            dateColumn: OrderDate
            baseValue: COUNT(*)
            """);

        Assert.Equal("${env:SQLFLOW_DW}", Assert.Single(doc.Connections).ConnectionRef);
    }

    [Fact]
    public void ConnectionConvention_IsTheOneRule()
    {
        Assert.Equal("SQLFLOW_CONN_DWH", ConnectionConvention.EnvironmentVariable("dwh"));
        Assert.Equal("${env:SQLFLOW_CONN_MY_DWH}", ConnectionConvention.Reference(" my-dwh "));
        Assert.Equal("${env:SQLFLOW_CONN_SHOP_2}", ConnectionConvention.Reference("shop.2"));
    }

    [Theory]
    [InlineData("Server=x;Database=y;User ID=u;Password=hunter2", true)]
    [InlineData("Server=x;Database=y;Uid=u;PWD=hunter2", true)]
    [InlineData("Host=pg;Username=u;password=p", true)]
    [InlineData("AccountEndpoint=x;AccessKey=abc", true)]
    [InlineData("Server=x;Database=y;Integrated Security=True", false)]
    [InlineData("Server=x;Authentication=Active Directory Default", false)]
    [InlineData("${env:SQLFLOW_CONN_DWH}", false)]
    [InlineData("${keyvault:vault/dw-conn}", false)]
    [InlineData("@registered-alias", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void SecretHygiene_RecognizesEmbeddedCredentials(string? reference, bool expected)
    {
        Assert.Equal(expected, SecretHygiene.LooksLikeEmbeddedSecret(reference));
    }

    [Fact]
    public void SecretHygiene_Warning_NamesAlternatives_NeverTheValue()
    {
        var warning = SecretHygiene.Warning("my-dwh", "flows/orders.flow.yaml");

        Assert.Contains("flows/orders.flow.yaml", warning, StringComparison.Ordinal);
        Assert.Contains("${env:SQLFLOW_CONN_MY_DWH}", warning, StringComparison.Ordinal);
        Assert.Contains(".sqlflow/env", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter", warning, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The .sqlflow/env loader: strict parsing, process-environment precedence, nearest-file discovery,
/// and the no-echo guarantee for malformed lines. File system and environment state are scoped per test.</summary>
public sealed class LocalEnvFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-envfile-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _setVariables = [];

    public LocalEnvFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var name in _setVariables)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Unique-per-test variable names, so parallel test runs never collide on process state.</summary>
    private string Var(string suffix)
    {
        var name = $"SQLFLOW_TEST_{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}_{suffix}";
        _setVariables.Add(name);
        return name;
    }

    private static string WriteEnvFile(string directory, string content)
    {
        var folder = Path.Combine(directory, ".sqlflow");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "env");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Apply_ParsesKeysCommentsBlanksAndQuotes()
    {
        var plain = Var("PLAIN");
        var quoted = Var("QUOTED");
        var path = WriteEnvFile(_root, $"""
            # local values
            {plain}=Server=localhost;Database=DW

            {quoted}=" spaced # value "
            """);

        var applied = LocalEnvFile.Apply(path);

        Assert.Equal([plain, quoted], applied);
        Assert.Equal("Server=localhost;Database=DW", Environment.GetEnvironmentVariable(plain));
        Assert.Equal(" spaced # value ", Environment.GetEnvironmentVariable(quoted));
    }

    [Fact]
    public void Apply_ProcessEnvironmentAlwaysWins()
    {
        var name = Var("PRECEDENCE");
        Environment.SetEnvironmentVariable(name, "from-ci");
        var path = WriteEnvFile(_root, $"{name}=from-file");

        var applied = LocalEnvFile.Apply(path);

        Assert.Empty(applied);
        Assert.Equal("from-ci", Environment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void Apply_MalformedLine_FailsWithLineNumber_WithoutEchoingContent()
    {
        var path = WriteEnvFile(_root, $"{Var("OK")}=fine\njust-a-secret-pasted-raw\n");

        var ex = Assert.Throws<SqlFlowException>(() => LocalEnvFile.Apply(path));
        Assert.Contains("(2)", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pasted-raw", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_InvalidVariableName_FailsClearly()
    {
        var path = WriteEnvFile(_root, "BAD NAME=x");

        var ex = Assert.Throws<SqlFlowException>(() => LocalEnvFile.Apply(path));
        Assert.Contains("not a valid environment variable name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyNearest_WalksUpToTheNearestFile()
    {
        var name = Var("NEAREST");
        WriteEnvFile(_root, $"{name}=found");
        var nested = Path.Combine(_root, "flows", "orders");
        Directory.CreateDirectory(nested);

        var (applied, file) = LocalEnvFile.ApplyNearest(nested);

        Assert.Equal([name], applied);
        Assert.Equal(Path.Combine(_root, ".sqlflow", "env"), file);
    }

    [Fact]
    public void ApplyNearest_NoFileAnywhere_IsANoOp()
    {
        var nested = Path.Combine(_root, "empty");
        Directory.CreateDirectory(nested);

        var (applied, file) = LocalEnvFile.ApplyNearest(nested);

        Assert.Empty(applied);
        Assert.Null(file);
    }
}
