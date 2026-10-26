using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The one rule a trigger definition is checked by, whether an administrator or the configuration gives it: what is
/// normalized (a bare branch, a bare workflow file, a pasted repository address) and what is refused, each with a reason
/// that names the trigger and the field.
/// </summary>
public sealed class ExternalTriggerDefinitionTests
{
    [Fact]
    public void A_GitHub_trigger_is_trimmed_and_normalized()
    {
        var definition = new ExternalTriggerDefinition(
            " deliver ", "GitHub", " estate ", "  ", false, ["nightly", " NIGHTLY ", "hourly"],
            "https://github.com/acme/estate.git", " 123 ", "main", "deliver.yml", " production ").Normalized();

        Assert.Equal("deliver", definition.Name);
        Assert.Equal(ExternalTriggerKinds.GitHub, definition.Kind);
        Assert.Equal("estate", definition.RepoName);
        Assert.Null(definition.Description);
        Assert.Equal(["nightly", "hourly"], definition.Schedules);
        Assert.Equal("acme/estate", definition.GitHubRepository);
        Assert.Equal("123", definition.GitHubRepositoryId);
        Assert.Equal("refs/heads/main", definition.GitHubRef);
        Assert.Equal(".github/workflows/deliver.yml", definition.GitHubWorkflow);
        Assert.Equal("production", definition.GitHubEnvironment);
    }

    [Theory]
    [InlineData("refs/tags/v*", "refs/tags/v*")]
    [InlineData("*", "*")]
    [InlineData("release/*", "refs/heads/release/*")]
    public void A_ref_keeps_its_full_form_and_a_bare_branch_gains_one(string given, string stored)
        => Assert.Equal(stored, GitHub(gitRef: given).Normalized().GitHubRef);

    [Theory]
    [InlineData("./.github/workflows/deliver.yml", ".github/workflows/deliver.yml")]
    [InlineData("ci/flows.yaml", "ci/flows.yaml")]
    public void A_workflow_is_a_repository_relative_path(string given, string stored)
        => Assert.Equal(stored, GitHub(workflow: given).Normalized().GitHubWorkflow);

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("slash/name")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890x")]
    public void A_name_outside_the_allowed_form_is_refused(string name)
        => Refused(GitHub(name: name), "A trigger's name");

    [Fact]
    public void An_unknown_kind_is_refused()
        => Refused(GitHub() with { Kind = "webhook" }, "kind 'webhook'");

    [Fact]
    public void A_trigger_needs_a_repository()
        => Refused(GitHub() with { RepoName = " " }, "needs the repository it syncs");

    [Fact]
    public void All_schedules_and_a_list_are_one_or_the_other()
        => Refused(GitHub() with { AllSchedules = true }, "leave its schedule list empty");

    [Fact]
    public void A_trigger_with_no_schedule_only_syncs()
    {
        var definition = GitHub() with { Schedules = [] };

        Assert.Empty(definition.Normalized().Schedules);
    }

    [Theory]
    [InlineData("estate")]
    [InlineData("acme/")]
    [InlineData("/estate")]
    [InlineData("acme/estate/extra")]
    [InlineData("ac me/estate")]
    public void A_GitHub_repository_is_owner_slash_name(string repository)
        => Refused(GitHub(repository: repository), "written owner/name");

    [Fact]
    public void A_repository_id_is_a_number()
        => Refused(GitHub(repositoryId: "R_kgDO"), "repository's number");

    [Theory]
    [InlineData("")]
    [InlineData("refs/heads/ma in")]
    public void A_GitHub_trigger_needs_a_ref(string gitRef)
        => Refused(GitHub(gitRef: gitRef), "needs the git ref");

    [Theory]
    [InlineData("/abs/deliver.yml")]
    [InlineData("../deliver.yml")]
    [InlineData(".github/workflows/deliver.yml@refs/heads/main")]
    [InlineData(".github/workflows/deliver.json")]
    public void A_workflow_outside_the_repository_or_not_a_workflow_file_is_refused(string workflow)
        => Refused(GitHub(workflow: workflow), "the workflow file's path");

    [Fact]
    public void A_key_trigger_carries_no_GitHub_field()
        => Refused(new ExternalTriggerDefinition("key", "key", "estate", null, false, [], GitHubRef: "refs/heads/main"), "key trigger");

    [Fact]
    public void A_key_trigger_is_a_name_a_repository_and_its_schedules()
    {
        var definition = new ExternalTriggerDefinition("ops-key", "KEY", "estate", "Operations", true, []).Normalized();

        Assert.Equal(ExternalTriggerKinds.Key, definition.Kind);
        Assert.True(definition.AllSchedules);
        Assert.Null(definition.GitHubRepository);
    }

    [Fact]
    public void A_configured_trigger_needs_exactly_one_of_GitHub_and_a_key_reference()
    {
        var both = Bootstrap(new ConfiguredExternalTriggerOptions
        {
            RepoSource = "estate",
            KeyReference = "${env:KEY}",
            GitHub = new ConfiguredGitHubTriggerOptions { Repository = "acme/estate", Ref = "main" },
        });
        var neither = Bootstrap(new ConfiguredExternalTriggerOptions { RepoSource = "estate" });

        Assert.Contains("exactly one of GitHub", Assert.Throws<InvalidOperationException>(both.ConfiguredExternalTriggers).Message, StringComparison.Ordinal);
        Assert.Contains("exactly one of GitHub", Assert.Throws<InvalidOperationException>(neither.ConfiguredExternalTriggers).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_key_is_a_reference_never_the_key()
    {
        var bootstrap = Bootstrap(new ConfiguredExternalTriggerOptions { RepoSource = "estate", KeyReference = "sqlft_literal-key-in-config" });

        var error = Assert.Throws<InvalidOperationException>(bootstrap.ConfiguredExternalTriggers);

        Assert.Contains("ControlPlane:Bootstrap:ExternalTriggers:deliver:KeyReference must be a secret reference", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_trigger_is_checked_by_the_same_rule_and_names_its_setting()
    {
        var bootstrap = Bootstrap(new ConfiguredExternalTriggerOptions
        {
            RepoSource = "estate",
            GitHub = new ConfiguredGitHubTriggerOptions { Repository = "not-a-repository", Ref = "main" },
        });

        var error = Assert.Throws<InvalidOperationException>(bootstrap.ConfiguredExternalTriggers);

        Assert.StartsWith("ControlPlane:Bootstrap:ExternalTriggers:deliver: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("owner/name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_configured_trigger_is_read_with_its_setting()
    {
        var bootstrap = Bootstrap(new ConfiguredExternalTriggerOptions
        {
            RepoSource = "estate",
            Schedules = ["nightly"],
            GitHub = new ConfiguredGitHubTriggerOptions { Repository = "acme/estate", Ref = "main", Workflow = "deliver.yml" },
        });

        var entry = Assert.Single(bootstrap.ConfiguredExternalTriggers());

        Assert.Equal("ControlPlane:Bootstrap:ExternalTriggers:deliver", entry.Setting);
        Assert.Null(entry.KeyReference);
        Assert.Equal("refs/heads/main", entry.Definition.GitHubRef);
        Assert.Equal(["nightly"], entry.Definition.Schedules);
    }

    [Theory]
    [InlineData(4, 30, 2)]
    [InlineData(60, 0, 2)]
    [InlineData(60, 30, 0)]
    [InlineData(60, 30, 61)]
    public void Out_of_range_settings_stop_the_start(int tokenMinutes, int syncTimeoutMinutes, int pollSeconds)
    {
        var options = new ExternalTriggerOptions { TokenMinutes = tokenMinutes, SyncTimeoutMinutes = syncTimeoutMinutes, PollSeconds = pollSeconds };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("http://token.actions.githubusercontent.com", "sqlflow")]
    [InlineData("https://token.actions.githubusercontent.com", "")]
    [InlineData("https://token.actions.githubusercontent.com", "has space")]
    public void An_unsafe_GitHub_setting_stops_the_start(string issuer, string audience)
    {
        var options = new GitHubTriggerOptions { Issuer = issuer, Audience = audience };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static ExternalTriggerDefinition GitHub(
        string name = "deliver", string repository = "acme/estate", string? repositoryId = null, string gitRef = "refs/heads/main",
        string? workflow = ".github/workflows/deliver.yml")
        => new(name, "github", "estate", null, false, ["nightly"], repository, repositoryId, gitRef, workflow, null);

    private static void Refused(ExternalTriggerDefinition definition, string reason)
    {
        var error = Assert.Throws<ExternalTriggerDefinitionException>(definition.Normalized);
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    private static BootstrapOptions Bootstrap(ConfiguredExternalTriggerOptions trigger)
        => new() { ExternalTriggers = new Dictionary<string, ConfiguredExternalTriggerOptions?>(StringComparer.OrdinalIgnoreCase) { ["deliver"] = trigger } };
}
