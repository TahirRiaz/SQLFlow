using System.Text.Json.Nodes;
using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant surface's pure rules: which strings are withheld, which SELECT a data-operations task is checked
/// as, how the MCP address is marked, and that the marker spelled in C# is the one the Rust MCP server sends.
/// </summary>
public sealed class AssistantSurfaceTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> EmployeeSsnDenied =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Employee"] = new HashSet<string>(["Ssn"], StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void Tokenize_SplitsQualifiedNamesAndReadsQuotedIdentifiers()
    {
        var tokens = AssistantScope.Tokenize("SELECT e.[Social Id], \"Ssn\" FROM dbo.Employee e WHERE x_1 = 2");
        Assert.Contains("Social Id", tokens);
        Assert.Contains("ssn", tokens);
        Assert.Contains("Employee", tokens);
        Assert.Contains("x_1", tokens);
        Assert.DoesNotContain("2", tokens);
    }

    [Fact]
    public void AStringNamingTheTableAndItsDeniedColumn_IsWithheld()
    {
        var node = JsonNode.Parse("""{ "sql": "SELECT Ssn, Name FROM dbo.Employee", "rows": 3 }""");
        var redacted = AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);
        Assert.Equal(1, withheld);
        Assert.Equal(AssistantScope.WithheldText, redacted!["sql"]!.GetValue<string>());
        Assert.Equal(3, redacted["rows"]!.GetValue<int>());
    }

    [Fact]
    public void AColumnNameBesideItsTableInTheSameObject_IsWithheld_ButTheTableNameIsKept()
    {
        var node = JsonNode.Parse("""{ "items": [ { "objectName": "Employee", "columnName": "SSN" } ] }""");
        var redacted = AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);
        Assert.Equal(1, withheld);
        Assert.Equal("Employee", redacted!["items"]![0]!["objectName"]!.GetValue<string>());
        Assert.Equal(AssistantScope.WithheldText, redacted["items"]![0]!["columnName"]!.GetValue<string>());
    }

    [Fact]
    public void TheSameColumnNameWithNoMentionOfItsTable_IsKept()
    {
        var node = JsonNode.Parse("""{ "sql": "SELECT Ssn FROM dbo.Contractor" }""");
        AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);
        Assert.Equal(0, withheld);
    }

    [Fact]
    public void ARootString_IsReplacedWhole()
    {
        var redacted = AssistantScope.Redact(JsonValue.Create("Column 'dbo.Employee.Ssn' is refused"), EmployeeSsnDenied, out var withheld);
        Assert.Equal(1, withheld);
        Assert.Equal(AssistantScope.WithheldText, redacted!.GetValue<string>());
    }

    [Fact]
    public void NothingDenied_ChangesNothing()
    {
        var node = JsonNode.Parse("""{ "sql": "SELECT Ssn FROM dbo.Employee" }""");
        var empty = new Dictionary<string, IReadOnlySet<string>>();
        Assert.Same(node, AssistantScope.Redact(node, empty, out var withheld));
        Assert.Equal(0, withheld);
    }

    [Fact]
    public void ADuplicateKeyTask_IsCheckedAsASelectOfItsKeyColumns_AndMustNameThem()
    {
        var named = Payload(ComputeOperations.DuplicateKeys) with { Columns = ["TripId", "Leg"] };
        Assert.Equal("SELECT [TripId], [Leg] FROM [dbo].[Trips]", ColumnPolicyGuard.ComposeTaskSelect(named));

        Assert.Throws<SqlFlowException>(() => ColumnPolicyGuard.ComposeTaskSelect(Payload(ComputeOperations.DuplicateKeys)));
    }

    [Fact]
    public void ABaselineComparison_IsCheckedByMode()
    {
        Assert.Throws<SqlFlowException>(() => ColumnPolicyGuard.ComposeTaskSelect(
            Payload(ComputeOperations.CompareBaseline) with { CompareMode = BaselineCompareMode.Inventory }));

        Assert.Equal("SELECT * FROM [dbo].[Trips]", ColumnPolicyGuard.ComposeTaskSelect(
            Payload(ComputeOperations.CompareBaseline) with { CompareMode = BaselineCompareMode.Schema }));

        Assert.Equal("SELECT CAST(Dato AS date), [Amount] FROM [dbo].[Trips] WHERE Dato >= '2026-01-01'", ColumnPolicyGuard.ComposeTaskSelect(
            Payload(ComputeOperations.CompareBaseline) with
            {
                CompareMode = BaselineCompareMode.Data,
                KeyExpressions = ["CAST(Dato AS date)"],
                CompareColumns = ["Amount"],
                Where = "Dato >= '2026-01-01'",
            }));

        Assert.Equal("SELECT TripId, * FROM [dbo].[Trips]", ColumnPolicyGuard.ComposeTaskSelect(
            Payload(ComputeOperations.CompareBaseline) with { CompareMode = BaselineCompareMode.Data, KeyExpressions = ["TripId"] }));
    }

    [Fact]
    public void AnyOtherOperation_IsNotAvailableToTheAssistant()
    {
        Assert.Throws<SqlFlowException>(() => ColumnPolicyGuard.ComposeTaskSelect(Payload(ComputeOperations.DetectUniqueKey)));
    }

    [Fact]
    public void TheAssistantMcpAddress_CarriesTheSurfaceQueryOnce()
    {
        Assert.Equal("https://mcp.example/mcp?surface=assistant",
            new McpOptions { ServerUrl = "https://mcp.example/mcp" }.AssistantServerUri.AbsoluteUri);
        Assert.Equal("http://localhost:8090/mcp?x=1&surface=assistant",
            new McpOptions { ServerUrl = "http://localhost:8090/mcp?x=1" }.AssistantServerUri.AbsoluteUri);
        Assert.Equal("https://mcp.example/mcp?surface=assistant",
            new McpOptions { ServerUrl = "https://mcp.example/mcp?surface=assistant" }.AssistantServerUri.AbsoluteUri);
    }

    [Fact]
    public void TheMcpServerSendsTheMarkerThisControlPlaneReads()
    {
        // The MCP server is Rust and the control plane C#; reading the one from the other keeps the cross-language
        // contract from drifting silently, which would switch the assistant narrowing off without any failure.
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "sqlflow-mcp", "src", "control_plane.rs"));
        Assert.Contains($"pub const SURFACE_HEADER: &str = \"{AssistantScope.HeaderName}\";", source, StringComparison.Ordinal);
        Assert.Contains($"pub const ASSISTANT_SURFACE: &str = \"{AssistantScope.AssistantValue}\";", source, StringComparison.Ordinal);
        Assert.Equal(McpOptions.AssistantSurfaceQuery, $"surface={AssistantScope.AssistantValue}");
    }

    private static ComputeTaskPayload Payload(string operation)
        => new() { Operation = operation, SourceRef = "${env:DW}", Schema = "dbo", ObjectName = "Trips" };
}
