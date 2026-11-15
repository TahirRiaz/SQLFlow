using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a run says started it, from what it recorded: the one rule and wording the runs board, a run's page, a run
/// group, the CLI and the MCP server share. Who asked, when the run names someone, decides over the trigger source, so a
/// run an older control plane recorded with the wrong source still says who started it.
/// </summary>
public sealed class RunStartedByTests
{
    private static readonly Guid ScheduleId = Guid.NewGuid();

    [Fact]
    public void A_schedules_own_fire_names_the_schedule()
    {
        var startedBy = Describe(RunTriggerSources.Schedule, "schedule:nightly", "nightly");

        Assert.Equal(RunStartedByKinds.Schedule, startedBy.Kind);
        Assert.Equal("Schedule nightly fired on its own.", startedBy.Summary);
        Assert.Null(startedBy.Who);
        Assert.Equal(ScheduleId, startedBy.ScheduleId);
    }

    [Fact]
    public void A_schedule_fire_whose_schedule_left_the_catalog_takes_the_name_its_run_recorded()
    {
        var startedBy = Describe(RunTriggerSources.Schedule, "schedule:gone", scheduleName: null);

        Assert.Equal("gone", startedBy.ScheduleName);
        Assert.Equal("Schedule gone fired on its own.", startedBy.Summary);
    }

    [Fact]
    public void A_chained_fire_names_the_schedule_it_followed()
    {
        var parentRun = Guid.NewGuid();
        var startedBy = RunStartedBy.Describe(new RunStartedBy.Facts(
            RunTriggerSources.Chain, "schedule:reference-data", ScheduleId, "reference-data", parentRun, "land-files",
            "land-files-00-copy", null));

        Assert.Equal(RunStartedByKinds.Chain, startedBy.Kind);
        Assert.Equal("Schedule reference-data fired after schedule land-files finished.", startedBy.Summary);
        Assert.Equal(parentRun, startedBy.AfterRunId);
        Assert.Equal("land-files-00-copy", startedBy.AfterFlowName);
    }

    [Fact]
    public void A_chained_fire_whose_parent_fire_ran_nothing_says_it_followed_its_parents()
    {
        var startedBy = Describe(RunTriggerSources.Chain, "schedule:reference-data", "reference-data");

        Assert.Equal("Schedule reference-data fired after its parent schedules finished.", startedBy.Summary);
        Assert.Null(startedBy.AfterRunId);
    }

    [Fact]
    public void A_run_now_names_the_person_and_the_schedule()
    {
        var startedBy = Describe(RunTriggerSources.Manual, "alice", "nightly");

        Assert.Equal(RunStartedByKinds.User, startedBy.Kind);
        Assert.Equal("alice", startedBy.Who);
        Assert.Equal("alice ran schedule nightly now.", startedBy.Summary);
    }

    [Fact]
    public void A_run_asked_for_directly_names_the_person()
    {
        var startedBy = RunStartedBy.Describe(new RunStartedBy.Facts(
            RunTriggerSources.Manual, "alice", null, null, null, null, null, null));

        Assert.Equal(RunStartedByKinds.User, startedBy.Kind);
        Assert.Equal("alice asked for this run.", startedBy.Summary);
        Assert.Null(startedBy.ScheduleId);
    }

    [Fact]
    public void A_caller_with_no_name_is_said_to_have_asked_never_the_schedule()
    {
        var startedBy = Describe(RunTriggerSources.Manual, RequestActor.Unknown, "nightly");

        Assert.Equal(RunStartedByKinds.User, startedBy.Kind);
        Assert.Null(startedBy.Who);
        Assert.Equal("Schedule nightly was run now by a caller whose sign-in carried no name.", startedBy.Summary);
    }

    [Fact]
    public void A_run_now_an_older_control_plane_recorded_as_a_schedule_fire_still_names_the_person()
    {
        var startedBy = Describe(RunTriggerSources.Schedule, "alice", "nightly");

        Assert.Equal(RunStartedByKinds.User, startedBy.Kind);
        Assert.Equal("alice ran schedule nightly now.", startedBy.Summary);
    }

    [Fact]
    public void An_external_trigger_names_itself_and_the_schedule_it_fired()
    {
        var request = Guid.NewGuid();
        var startedBy = RunStartedBy.Describe(new RunStartedBy.Facts(
            RunTriggerSources.Manual, "trigger:merge-to-main", ScheduleId, "nightly", null, null, null, request));

        Assert.Equal(RunStartedByKinds.Trigger, startedBy.Kind);
        Assert.Equal("merge-to-main", startedBy.Who);
        Assert.Equal("External trigger merge-to-main fired schedule nightly.", startedBy.Summary);
        Assert.Equal(request, startedBy.TriggerRequestId);
    }

    [Fact]
    public void A_command_line_run_names_its_account_when_it_recorded_one()
    {
        Assert.Equal(
            ("tahir@build-01", "Run from the command line by tahir@build-01."),
            Pair(RunStartedBy.Describe(new RunStartedBy.Facts(RunTriggerSources.Cli, "cli:tahir@build-01", null, null, null, null, null, null))));
        Assert.Equal(
            ((string?)null, "Run from the command line, and recorded from its run file."),
            Pair(RunStartedBy.Describe(new RunStartedBy.Facts(RunTriggerSources.Cli, null, null, null, null, null, null, null))));
    }

    [Fact]
    public void A_run_from_before_the_record_says_so()
    {
        var startedBy = RunStartedBy.Describe(new RunStartedBy.Facts(null, null, null, null, null, null, null, null));

        Assert.Equal(RunStartedByKinds.Unknown, startedBy.Kind);
        Assert.Equal("Not recorded: the run predates the record of what starts a run.", startedBy.Summary);
    }

    private static RunStartedByDto Describe(string? source, string? requestedBy, string? scheduleName)
        => RunStartedBy.Describe(new RunStartedBy.Facts(source, requestedBy, ScheduleId, scheduleName, null, null, null, null));

    private static (string? Who, string Summary) Pair(RunStartedByDto startedBy) => (startedBy.Who, startedBy.Summary);
}
