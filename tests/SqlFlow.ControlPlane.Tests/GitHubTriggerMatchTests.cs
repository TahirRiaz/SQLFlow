using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Which trigger a GitHub job acts as: the repository, its pinned id, the ref, the workflow and the environment each
/// trigger asks for against what the job's token states, and the refusals that say exactly which part did not match.
/// </summary>
public sealed class GitHubTriggerMatchTests
{
    [Theory]
    [InlineData("refs/heads/main", "refs/heads/main", true)]
    [InlineData("refs/heads/main", "refs/heads/main2", false)]
    [InlineData("refs/heads/main", "refs/heads/Main", false)]
    [InlineData("refs/heads/release/*", "refs/heads/release/2026.10", true)]
    [InlineData("refs/heads/release/*", "refs/heads/release/", true)]
    [InlineData("refs/heads/release/*", "refs/heads/feature/x", false)]
    [InlineData("refs/tags/v*", "refs/tags/v1.2.3", true)]
    [InlineData("*", "refs/pull/7/merge", true)]
    [InlineData("refs/*/main", "refs/heads/main", true)]
    [InlineData("refs/*/main", "refs/heads/x/main", true)]
    [InlineData("refs/*/main", "refs/heads/main/x", false)]
    [InlineData("**", "anything", true)]
    [InlineData("", "refs/heads/main", false)]
    public void A_ref_pattern_matches_with_stars_as_any_run_of_characters(string pattern, string value, bool expected)
        => Assert.Equal(expected, GitHubTriggerMatch.GlobMatches(pattern, value));

    [Fact]
    public void The_one_trigger_that_trusts_the_job_is_chosen()
    {
        var trusted = Trigger("deliver");
        var otherRef = Trigger("release", gitRef: "refs/heads/release/*");

        var choice = GitHubTriggerMatch.Choose([trusted, otherRef], Job(), requestedName: null);

        Assert.Same(trusted, choice.Trigger);
    }

    [Fact]
    public void The_repository_compares_without_regard_to_case_as_GitHub_does()
    {
        var trigger = Trigger("deliver", repository: "Acme/Estate");

        Assert.Null(GitHubTriggerMatch.Mismatch(trigger, Job()));
    }

    [Fact]
    public void A_pinned_repository_id_must_match()
    {
        var trigger = Trigger("deliver", repositoryId: "999");

        var reason = GitHubTriggerMatch.Mismatch(trigger, Job());

        Assert.Contains("pinned to repository id 999", reason, StringComparison.Ordinal);
        Assert.Contains("has id 123456", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_workflow_must_be_the_one_the_job_runs()
    {
        var trigger = Trigger("deliver", workflow: ".github/workflows/nightly.yml");

        var reason = GitHubTriggerMatch.Mismatch(trigger, Job());

        Assert.Contains("trusts workflow .github/workflows/nightly.yml", reason, StringComparison.Ordinal);
        Assert.Contains("runs .github/workflows/deliver.yml", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trigger_that_names_no_workflow_trusts_every_workflow_of_the_repository()
        => Assert.Null(GitHubTriggerMatch.Mismatch(Trigger("deliver", workflow: null), Job()));

    [Fact]
    public void An_environment_asked_for_tells_a_job_without_one_how_to_set_it()
    {
        var trigger = Trigger("deliver", environment: "production");

        var reason = GitHubTriggerMatch.Mismatch(trigger, Job(environment: null));

        Assert.Contains("set 'environment: production' on the job", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_no_trigger_trusts_is_told_what_each_trigger_of_its_repository_asks_for()
    {
        var choice = GitHubTriggerMatch.Choose(
            [Trigger("deliver", gitRef: "refs/heads/release"), Trigger("other", repository: "acme/elsewhere")], Job(), requestedName: null);

        Assert.Null(choice.Trigger);
        Assert.Equal(403, choice.Status);
        Assert.Contains("'deliver' (it trusts ref refs/heads/release, and the job runs on refs/heads/main)", choice.Problem, StringComparison.Ordinal);
        // Triggers of other repositories are none of the job's business.
        Assert.DoesNotContain("other", choice.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_whose_repository_no_trigger_trusts_is_told_to_ask_an_administrator()
    {
        var choice = GitHubTriggerMatch.Choose([Trigger("other", repository: "acme/elsewhere")], Job(), requestedName: null);

        Assert.Equal(403, choice.Status);
        Assert.Contains("No enabled trigger trusts the repository acme/estate", choice.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_triggers_trusting_one_job_must_be_told_apart_by_name()
    {
        var first = Trigger("first");
        var second = Trigger("second");

        var ambiguous = GitHubTriggerMatch.Choose([first, second], Job(), requestedName: null);
        var named = GitHubTriggerMatch.Choose([first, second], Job(), requestedName: "SECOND");

        Assert.Equal(409, ambiguous.Status);
        Assert.Contains("first, second", ambiguous.Problem, StringComparison.Ordinal);
        Assert.Same(second, named.Trigger);
    }

    [Fact]
    public void A_named_trigger_that_does_not_trust_the_job_says_why()
    {
        var choice = GitHubTriggerMatch.Choose([Trigger("deliver", gitRef: "refs/heads/prod")], Job(), requestedName: "deliver");

        Assert.Equal(403, choice.Status);
        Assert.Contains("Trigger 'deliver' does not trust this job: it trusts ref refs/heads/prod", choice.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_trigger_of_another_repository_is_unknown_to_the_job()
    {
        var choice = GitHubTriggerMatch.Choose([Trigger("deliver", repository: "acme/elsewhere")], Job(), requestedName: "deliver");

        Assert.Equal(403, choice.Status);
        Assert.Contains("No enabled trigger named 'deliver' trusts acme/estate", choice.Problem, StringComparison.Ordinal);
    }

    private static GitHubActionsIdentity Job(string? environment = "production")
        => new("acme/estate", "123456", "acme", "42", "refs/heads/main", "0123456789abcdef0123456789abcdef01234567",
            "acme/estate/.github/workflows/deliver.yml@refs/heads/main", null, environment, "push", "1", "1", "octocat", null);

    private static CatalogExternalTrigger Trigger(
        string name, string repository = "acme/estate", string? repositoryId = null, string gitRef = "refs/heads/main",
        string? workflow = ".github/workflows/deliver.yml", string? environment = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = ExternalTriggerKinds.GitHub,
            RepoName = "estate",
            Enabled = true,
            GitHubRepository = repository,
            GitHubRepositoryId = repositoryId,
            GitHubRef = gitRef,
            GitHubWorkflow = workflow,
            GitHubEnvironment = environment,
        };
}
