using Microsoft.Extensions.Configuration;
using SqlFlow.Assistant;
using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// How AI context is turned on: one switch, <c>ControlPlane:AiContext:Enabled</c>, named as the GUI names the feature,
/// covering a question's query, saved answers and running a trusted saved answer; tuning under the same name with
/// defaults that never stop the control plane from starting; and every error naming the key to change.
/// </summary>
public sealed class AiContextOptionsTests
{
    [Fact]
    public void OneSwitch_TurnsItOn_WithAutoRunOn_AndNothingElseToSet()
    {
        var options = Bind(new Dictionary<string, string?> { ["ControlPlane:AiContext:Enabled"] = "true" });

        Assert.True(options.AiContext.Enabled);
        Assert.True(options.AiContext.AutoRun.Enabled);
        Assert.False(options.DataOps.Enabled);
        // Starts without an Anthropic key: expansion follows the key rather than stopping startup.
        options.AiContext.Validate(new AnthropicOptions());
        Assert.False(options.AiContext.ExpandsSynonyms(new AnthropicOptions()));
    }

    [Fact]
    public void ItShipsOff()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.False(options.AiContext.Enabled);
        Assert.False(options.AiContext.ExpandsSynonyms(new AnthropicOptions { ApiKey = "${env:ANTHROPIC_KEY}" }));
    }

    [Theory]
    [InlineData(null, "", false)]
    [InlineData(null, "${env:ANTHROPIC_KEY}", true)]
    [InlineData("false", "${env:ANTHROPIC_KEY}", false)]
    [InlineData("true", "${env:ANTHROPIC_KEY}", true)]
    public void SynonymExpansion_FollowsTheKey_UnlessSet(string? setting, string key, bool expected)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["ControlPlane:AiContext:Enabled"] = "true",
            ["ControlPlane:AiContext:ExpandSynonyms"] = setting,
        });

        Assert.Equal(expected, options.AiContext.ExpandsSynonyms(new AnthropicOptions { ApiKey = key }));
    }

    [Fact]
    public void ExpansionAskedForWithoutAKey_IsRefused_NamingBothKeys()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["ControlPlane:AiContext:Enabled"] = "true",
            ["ControlPlane:AiContext:ExpandSynonyms"] = "true",
        });

        var refused = Assert.Throws<InvalidOperationException>(() => options.AiContext.Validate(new AnthropicOptions()));

        Assert.Contains("ControlPlane:AiContext:ExpandSynonyms", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ControlPlane:Assistant:Anthropic:ApiKey", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TuningOutOfRange_IsRefused_NamingTheAiContextKeys()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["ControlPlane:AiContext:Enabled"] = "true",
            ["ControlPlane:AiContext:SavedAnswers:TopK"] = "0",
            ["ControlPlane:AiContext:AutoRun:TimeoutSeconds"] = "60",
        });

        var refused = Assert.Throws<InvalidOperationException>(() => options.AiContext.Validate(new AnthropicOptions()));

        Assert.Contains("ControlPlane:AiContext:SavedAnswers:TopK", refused.Message, StringComparison.Ordinal);
        Assert.Contains("ControlPlane:AiContext:AutoRun:TimeoutSeconds", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerAI", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SavedAnswersAreSentFromHalfCoverage_ByDefault()
    {
        var options = Bind(new Dictionary<string, string?> { ["ControlPlane:AiContext:Enabled"] = "true" });

        Assert.Equal(0.5, options.AiContext.SavedAnswers.MinCoverage);
        options.AiContext.Validate(new AnthropicOptions());
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.5")]
    [InlineData("NaN")]
    public void ACoverageFloorOutsideZeroToOne_IsRefused_NamingTheKey(string floor)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["ControlPlane:AiContext:Enabled"] = "true",
            ["ControlPlane:AiContext:SavedAnswers:MinCoverage"] = floor,
        });

        var refused = Assert.Throws<InvalidOperationException>(() => options.AiContext.Validate(new AnthropicOptions()));

        Assert.Contains("ControlPlane:AiContext:SavedAnswers:MinCoverage", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoRunTurnedOff_IsNotValidated_AndTheRestStillRuns()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["ControlPlane:AiContext:Enabled"] = "true",
            ["ControlPlane:AiContext:AutoRun:Enabled"] = "false",
            ["ControlPlane:AiContext:AutoRun:TimeoutSeconds"] = "60",
        });

        options.AiContext.Validate(new AnthropicOptions());
        Assert.False(options.AiContext.AutoRun.Enabled);
    }

    [Fact]
    public void ReportExtraction_LivesUnderAiContext_AndIsCheckedWhetherOrNotAiContextIsOn()
    {
        var options = Bind(new Dictionary<string, string?> { ["ControlPlane:AiContext:ReportExtraction:Enabled"] = "true" });

        var refused = Assert.Throws<InvalidOperationException>(() => options.AiContext.Validate(new AnthropicOptions()));

        Assert.Contains("ControlPlane:AiContext:ReportExtraction:Endpoint", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The options as the host binds them: the <c>ControlPlane</c> section of <paramref name="settings"/>.</summary>
    private static ControlPlaneOptions Bind(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Where(pair => pair.Value is not null))
            .Build();
        var options = new ControlPlaneOptions();
        configuration.GetSection("ControlPlane").Bind(options);
        return options;
    }
}
