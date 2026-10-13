using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Compute;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant surface's pure rules: how the marker is read, which SELECT a data-operations task is checked as, how
/// the MCP address is marked, and that the marker spelled in C# is the one the Rust MCP server sends.
/// </summary>
public sealed class AssistantSurfaceTests
{
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
            new McpOptions { ServerUrl = "https://mcp.example/mcp" }.AssistantServerUri(null).AbsoluteUri);
        Assert.Equal("http://localhost:8090/mcp?x=1&surface=assistant",
            new McpOptions { ServerUrl = "http://localhost:8090/mcp?x=1" }.AssistantServerUri(null).AbsoluteUri);
        Assert.Equal("https://mcp.example/mcp?surface=assistant",
            new McpOptions { ServerUrl = "https://mcp.example/mcp?surface=assistant" }.AssistantServerUri(null).AbsoluteUri);
    }

    [Fact]
    public void TheAssistantMcpAddress_NamesTheConversationsMode_ReplacingAnyTheConfigurationNamed()
    {
        Assert.Equal("https://mcp.example/mcp?surface=assistant&mode=data",
            new McpOptions { ServerUrl = "https://mcp.example/mcp" }.AssistantServerUri(AssistantMode.Data).AbsoluteUri);
        Assert.Equal("https://mcp.example/mcp?x=1&surface=assistant&mode=estate",
            new McpOptions { ServerUrl = "https://mcp.example/mcp?mode=data&x=1" }.AssistantServerUri(AssistantMode.Estate).AbsoluteUri);
        Assert.Equal("https://mcp.example/mcp?x=1&surface=assistant",
            new McpOptions { ServerUrl = "https://mcp.example/mcp?mode=data&x=1" }.AssistantServerUri(null).AbsoluteUri);
    }

    [Fact]
    public void TheMcpServerReadsTheModesThisControlPlaneNames()
    {
        // The MCP server defines the modes; this control plane only names one on the address, so the two spellings are
        // held together here, read from the server's source.
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "sqlflow-mcp", "src", "modes.rs"));
        Assert.Contains($"(\"{McpOptions.ModeQueryName}\", \"data\") => Some(Mode::Data)", source, StringComparison.Ordinal);
        Assert.Contains($"(\"{McpOptions.ModeQueryName}\", \"estate\") => Some(Mode::Estate)", source, StringComparison.Ordinal);
        // Both read the person's commands by one grammar (the shared table holds them to it); the words are spelled here.
        Assert.Contains($"pub const COMMAND: char = '{AssistantQuestions.Command}';", source, StringComparison.Ordinal);
        Assert.Contains($"const DATA_WORD: &str = \"{AssistantQuestions.CommandWord}\";", source, StringComparison.Ordinal);
        Assert.Contains(
            $"const EXIT_WORDS: &[&str] = &[\"{AssistantQuestions.ExitWord}\", \"{AssistantQuestions.EstateWord}\"];",
            source, StringComparison.Ordinal);
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
