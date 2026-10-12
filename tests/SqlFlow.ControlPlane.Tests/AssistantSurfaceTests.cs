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

    [Theory]
    // A ]] inside brackets and a "" inside quotes are one character of the name.
    [InlineData("SELECT [Pay]]Grade] FROM t", "Pay]Grade")]
    [InlineData("SELECT \"Pay\"\"Grade\" FROM t", "Pay\"Grade")]
    // A name that starts with digits is a name once a letter follows them.
    [InlineData("Employee.2FA_Code", "2FA_Code")]
    // Letters of any script.
    [InlineData("SELECT Omsætning FROM Salg", "Omsætning")]
    public void Tokenize_ReadsEscapesDigitLedNamesAndAnyScript(string text, string expected)
    {
        Assert.Contains(expected, AssistantScope.Tokenize(text));
    }

    [Theory]
    [InlineData("SELECT 2, 3.5, 1e3 FROM t")]
    public void Tokenize_LeavesPlainNumbersOut(string text)
    {
        var tokens = AssistantScope.Tokenize(text);
        Assert.DoesNotContain("2", tokens);
        Assert.DoesNotContain("3", tokens);
        Assert.DoesNotContain("5", tokens);
    }

    [Fact]
    public void Tokenize_DropsAnIdentifierLongerThanSqlServerAllows()
    {
        var tooLong = new string('a', 129);
        Assert.DoesNotContain(tooLong, AssistantScope.Tokenize(tooLong + " Employee"));
        Assert.Contains("Employee", AssistantScope.Tokenize(tooLong + " Employee"));
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SpacedAndEscapedDenied =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Employee"] = new HashSet<string>(["Social Security", "Pay]Grade", "2FA_Code", "Ssn"], StringComparer.OrdinalIgnoreCase),
        };

    [Theory]
    // Unquoted, a name with a space is caught as the whole string it is.
    [InlineData("""{ "objectName": "Employee", "columnName": "Social Security" }""")]
    [InlineData("""{ "objectName": "Employee", "columnName": "  social security " }""")]
    // Quoted, it is one identifier wherever it sits.
    [InlineData("""{ "sql": "SELECT [Social Security] FROM dbo.Employee" }""")]
    [InlineData("""{ "sql": "SELECT [Pay]]Grade] FROM dbo.Employee" }""")]
    [InlineData("""{ "objectName": "Employee", "columnName": "2FA_Code" }""")]
    public void AColumnNameTheTokenizerWouldSplit_IsStillWithheld(string json)
    {
        var node = JsonNode.Parse(json);
        AssistantScope.Redact(node, SpacedAndEscapedDenied, out var withheld);
        Assert.Equal(1, withheld);
    }

    [Fact]
    public void APropertyNamedForADeniedColumn_IsWithheldNameAndAll()
    {
        // A response keyed by column names says them in its keys.
        var node = JsonNode.Parse("""{ "object": "Employee", "stats": { "Ssn": { "nulls": 0 }, "Name": { "nulls": 2 } } }""");
        var redacted = AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);

        Assert.Equal(1, withheld);
        var stats = redacted!["stats"]!.AsObject();
        Assert.False(stats.ContainsKey("Ssn"));
        Assert.True(stats.ContainsKey("Name"));
        Assert.Equal(AssistantScope.WithheldText, stats[AssistantScope.WithheldText]!.GetValue<string>());
    }

    [Fact]
    public void AnObjectNamedInAPropertyName_IsContextForItsMembers()
    {
        var node = JsonNode.Parse("""{ "Employee": { "columns": [ "Ssn", "Name" ] } }""");
        var redacted = AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);

        Assert.Equal(1, withheld);
        Assert.Equal(AssistantScope.WithheldText, redacted!["Employee"]!["columns"]![0]!.GetValue<string>());
        Assert.Equal("Name", redacted["Employee"]!["columns"]![1]!.GetValue<string>());
    }

    [Fact]
    public void SeveralWithheldProperties_KeepDistinctNames()
    {
        var denied = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Employee"] = new HashSet<string>(["Ssn", "Salary"], StringComparer.OrdinalIgnoreCase),
        };
        var node = JsonNode.Parse("""{ "table": "Employee", "Ssn": 1, "Salary": 2, "Name": 3 }""");
        var redacted = AssistantScope.Redact(node, denied, out var withheld)!.AsObject();

        Assert.Equal(2, withheld);
        Assert.Equal(4, redacted.Count);
        Assert.True(redacted.ContainsKey(AssistantScope.WithheldText));
        Assert.True(redacted.ContainsKey($"{AssistantScope.WithheldText} (2)"));
    }

    [Fact]
    public void ContextIsInherited_ButNeverSharedBetweenSiblings()
    {
        var inherited = JsonNode.Parse("""{ "object": "Employee", "kids": [ { "cols": [ "Ssn" ] } ] }""");
        AssistantScope.Redact(inherited, EmployeeSsnDenied, out var fromAbove);
        Assert.Equal(1, fromAbove);

        var siblings = JsonNode.Parse("""{ "table": { "name": "Employee" }, "columns": [ "Ssn" ] }""");
        AssistantScope.Redact(siblings, EmployeeSsnDenied, out var fromBeside);
        Assert.Equal(0, fromBeside);
    }

    [Theory]
    [InlineData("""{ "objectName": "EMPLOYEE", "columnName": "ssn" }""")]
    [InlineData("""{ "key": "srv|dw|dbo|employee|ssn" }""")]
    [InlineData("""{ "sql": "select e.SSN from DBO.employee e" }""")]
    public void MatchingIgnoresCase_AndReadsPipeKeys(string json)
    {
        AssistantScope.Redact(JsonNode.Parse(json), EmployeeSsnDenied, out var withheld);
        Assert.Equal(1, withheld);
    }

    [Fact]
    public void NumbersBooleansAndNulls_AreLeftAlone()
    {
        var node = JsonNode.Parse("""{ "object": "Employee", "count": 3, "ok": true, "none": null, "values": [1, 2] }""");
        var redacted = AssistantScope.Redact(node, EmployeeSsnDenied, out var withheld);

        Assert.Equal(0, withheld);
        Assert.Equal(3, redacted!["count"]!.GetValue<int>());
        Assert.True(redacted["ok"]!.GetValue<bool>());
        Assert.Null(redacted["none"]);
    }

    [Theory]
    [InlineData(" assistant ", true)]
    [InlineData("ASSISTANT", true)]
    [InlineData("assistant,x", false)]
    [InlineData("gui", false)]
    public void IsAssistant_ReadsTheMarkerTrimmedAndWithoutCase(string value, bool expected)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Headers[AssistantScope.HeaderName] = value;
        Assert.Equal(expected, AssistantScope.IsAssistant(http));
    }

    [Fact]
    public void IsAssistant_FindsTheMarkerAmongSeveralValues_AndNotWithoutOne()
    {
        var several = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        several.Request.Headers[AssistantScope.HeaderName] = new Microsoft.Extensions.Primitives.StringValues(["x", "assistant"]);
        Assert.True(AssistantScope.IsAssistant(several));
        Assert.False(AssistantScope.IsAssistant(new Microsoft.AspNetCore.Http.DefaultHttpContext()));
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
