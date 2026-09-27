using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// The labels a run records as the one that asked for it when no person asked through the API: a schedule's fire and a
/// direct CLI run. Each must say what started the run and fit the catalog column whatever the names it is built from.
/// </summary>
public sealed class RunActorsTests
{
    private static readonly Guid ScheduleId = Guid.Parse("0b6f3f6c-5a55-4f1e-9d1e-8c3a1f0e2b7d");

    [Fact]
    public void Schedule_IsNamedByItsName()
        => Assert.Equal("schedule:recall-welllog", RunActors.Schedule("  recall-welllog ", ScheduleId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Schedule_WithoutAName_IsNamedByItsId(string? name)
        => Assert.Equal("schedule:0b6f3f6c-5a55-4f1e-9d1e-8c3a1f0e2b7d", RunActors.Schedule(name, ScheduleId));

    [Fact]
    public void Schedule_WhoseNameDoesNotFit_IsNamedByItsId_NotCut()
    {
        // The catalog allows a schedule name longer than the column: a cut name could name another schedule, the id cannot.
        var label = RunActors.Schedule(new string('s', RunActors.MaxLength), ScheduleId);

        Assert.Equal("schedule:0b6f3f6c-5a55-4f1e-9d1e-8c3a1f0e2b7d", label);
    }

    [Fact]
    public void Schedule_WhoseNameJustFits_KeepsIt()
    {
        var name = new string('s', RunActors.MaxLength - RunActors.SchedulePrefix.Length);

        Assert.Equal(RunActors.SchedulePrefix + name, RunActors.Schedule(name, ScheduleId));
        Assert.Equal(RunActors.MaxLength, RunActors.Schedule(name, ScheduleId).Length);
    }

    [Theory]
    [InlineData("tahir", "CHESS", "cli:tahir@CHESS")]
    [InlineData(" tahir ", " CHESS ", "cli:tahir@CHESS")]
    [InlineData("tahir", "", "cli:tahir")]
    [InlineData("tahir", null, "cli:tahir")]
    [InlineData("", "CHESS", "cli:@CHESS")]
    [InlineData(null, null, "cli:local")]
    [InlineData("  ", "  ", "cli:local")]
    [InlineData("Ærø ünïcode", "hôte", "cli:Ærø ünïcode@hôte")]
    public void Cli_NamesTheAccountAndTheMachine(string? user, string? machine, string expected)
        => Assert.Equal(expected, RunActors.Cli(user, machine));

    [Fact]
    public void Cli_ShortensALongUser_AndKeepsTheMachine()
    {
        var label = RunActors.Cli(new string('u', 400), "CHESS");

        Assert.Equal(RunActors.MaxLength, label.Length);
        Assert.StartsWith("cli:uuu", label, StringComparison.Ordinal);
        Assert.EndsWith("@CHESS", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_WithAMachineNameLongerThanTheColumn_StillFits()
    {
        var label = RunActors.Cli("tahir", new string('m', 400));

        Assert.Equal(RunActors.MaxLength, label.Length);
        Assert.StartsWith("cli:@mmm", label, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalAccount_IsACliLabel_ThatFits()
    {
        var label = RunActors.LocalAccount();

        Assert.StartsWith(RunActors.CliPrefix, label, StringComparison.Ordinal);
        Assert.InRange(label.Length, RunActors.CliPrefix.Length + 1, RunActors.MaxLength);
    }
}
