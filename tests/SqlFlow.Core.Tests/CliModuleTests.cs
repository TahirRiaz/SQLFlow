using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A host composes the CLI with modules through <see cref="ICliModule"/>: a module's verbs run with the parsed command line
/// (its own value-taking options included) and the command's service provider holding the module's registrations, appear in
/// the help and in every shell's completion script, and never change how a SQLFlow verb parses. A module that breaks a rule
/// is refused before anything runs, with the module named.
/// </summary>
public sealed class CliModuleTests : IDisposable
{
    private readonly string _anchor = Directory.CreateTempSubdirectory("sqlflow-cli-module-").FullName;

    public void Dispose() => Directory.Delete(_anchor, recursive: true);

    [Fact]
    public async Task AModuleVerb_RunsWithItsArguments_AndTheModuleServices()
    {
        var module = new ProbeModule();

        var exit = await CliHost.RunAsync(["--label", "first", "probe", _anchor, "--set", "a=1", "--set", "b=2", "--connect", "--json"], module);

        Assert.Equal(7, exit);
        var seen = Assert.Single(module.Calls);
        Assert.Equal("probe", seen.ModuleName);
        Assert.Equal("probe", seen.Verb);
        Assert.Equal(["probe", _anchor], seen.Arguments.Positionals);
        Assert.Equal("first", seen.Arguments.GetOption("--label"));
        Assert.Equal(["a=1", "b=2"], seen.Arguments.GetOptions("--set"));
        Assert.True(seen.Arguments.HasFlag("--connect"));
        Assert.True(seen.Json);
        Assert.Equal("registered by probe for Command", seen.ServiceDescription);
    }

    [Fact]
    public async Task AModuleVerbThrowingASqlFlowError_ExitsWithOne()
    {
        var module = new ProbeModule { Failure = new SqlFlowException("the probe refused") };

        var exit = await CliHost.RunAsync(["probe", _anchor], module);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task AModuleWhoseServiceRegistrationFails_StopsTheCommand()
    {
        var module = new ProbeModule { ConfigureFailure = new InvalidOperationException("no services today") };

        var exit = await CliHost.RunAsync(["probe", _anchor], module);

        Assert.Equal(1, exit);
        Assert.Empty(module.Calls);
    }

    [Fact]
    public void AModuleValueOption_DoesNotChangeHowASqlFlowVerbParses()
    {
        var modules = new CliModuleSet([new ProbeModule()]);

        Assert.Equal(["run", "pipe.yaml"], modules.PositionalArguments(["run", "--label", "pipe.yaml"]));
        Assert.Equal(["probe", "target"], modules.PositionalArguments(["probe", "--label", "x", "target"]));
        Assert.Equal(["probe", "target"], modules.PositionalArguments(["--label", "x", "probe", "target"]));
        Assert.Equal(Program.PositionalArguments(["run", "--label", "pipe.yaml"]), CliModuleSet.Empty.PositionalArguments(["run", "--label", "pipe.yaml"]));
    }

    [Fact]
    public void TheHelp_ListsModuleVerbsAfterTheLocalVerbs_AndIsUnchangedWithoutModules()
    {
        var plain = Program.UsageText(CliModuleSet.Empty);
        var withModule = Program.UsageText(new CliModuleSet([new ProbeModule()]));

        Assert.StartsWith("sqlflow - metadata-driven ETL for SQL Server", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlflow probe", plain, StringComparison.Ordinal);
        var probe = withModule.IndexOf("  sqlflow probe <target> [--label <l>]", StringComparison.Ordinal);
        Assert.True(probe > withModule.IndexOf("sqlflow user reset-password", StringComparison.Ordinal));
        Assert.True(probe < withModule.IndexOf("Control plane (remote)", StringComparison.Ordinal));
        Assert.Equal(plain.Length + ProbeModule.UsageLines.Sum(l => l.Length + 2 + Environment.NewLine.Length), withModule.Length);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("zsh")]
    [InlineData("powershell")]
    public void EveryCompletionScript_OffersTheModuleVerbSubcommandsAndOptions(string shell)
    {
        var script = CliCompletions.Script(shell, new CliModuleSet([new ProbeModule()]));

        Assert.NotNull(script);
        Assert.Contains("probe", script, StringComparison.Ordinal);
        Assert.Contains("inspect", script, StringComparison.Ordinal);
        Assert.Contains("--label", script, StringComparison.Ordinal);
        Assert.Contains("--connect", script, StringComparison.Ordinal);
        Assert.Contains("validate", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownShell_HasNoCompletionScript()
        => Assert.Null(CliCompletions.Script("fish", CliModuleSet.Empty));

    [Fact]
    public void AModuleVerbNamedLikeASqlFlowVerb_IsRefused()
    {
        var error = Assert.Throws<CliModuleException>(() => new CliModuleSet([new VerbModule("clash", Verb("run"))]));

        Assert.Equal("clash", error.ModuleName);
        Assert.Contains("'run', which is a SQLFlow verb", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoModulesAddingOneVerb_AreRefused_NamingBoth()
    {
        var error = Assert.Throws<CliModuleException>(
            () => new CliModuleSet([new VerbModule("first", Verb("shared")), new VerbModule("second", Verb("shared"))]));

        Assert.Equal("second", error.ModuleName);
        Assert.Contains("module 'first' already adds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoModulesWithOneName_AreRefused()
    {
        var error = Assert.Throws<CliModuleException>(
            () => new CliModuleSet([new VerbModule("twin", Verb("one")), new VerbModule("twin", Verb("two"))]));

        Assert.Contains("registered twice", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, CliVerb, string> BrokenVerbs => new()
    {
        { "invalid verb name", Verb("Check"), "a verb is lowercase letters" },
        { "no usage", new CliVerb("check", [], _ => Task.FromResult(0)), "at least one usage line" },
        { "multi-line usage", new CliVerb("check", ["sqlflow check\nmore"], _ => Task.FromResult(0)), "at least one usage line" },
        { "repeated subcommand", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { Subcommands = ["list", "list"] }, "declares the subcommand 'list'" },
        { "invalid subcommand", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { Subcommands = ["List"] }, "declares the subcommand 'List'" },
        { "invalid option", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { ValueOptions = ["kind"] }, "declares the option 'kind'" },
        { "option with a quote", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { Flags = ["--x'y"] }, "declares the option" },
        { "shared flag takes a value", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { ValueOptions = ["--json"] }, "a flag every verb shares" },
        { "value option and flag", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { ValueOptions = ["--kind"], Flags = ["--kind"] }, "both as taking a value and as a flag" },
        { "flag SQLFlow reads with a value", new CliVerb("check", ["sqlflow check"], _ => Task.FromResult(0)) { Flags = ["--db"] }, "SQLFlow reads it as taking a value" },
    };

    [Theory]
    [MemberData(nameof(BrokenVerbs))]
    public void AVerbBreakingARule_IsRefused_NamingTheModule(string rule, CliVerb verb, string message)
    {
        var error = Assert.Throws<CliModuleException>(() => new CliModuleSet([new VerbModule("broken", verb)]));

        Assert.True(error.ModuleName == "broken", rule);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Probe")]
    [InlineData("-probe")]
    [InlineData("probe module")]
    public void AnInvalidModuleName_IsRefusedBeforeAnythingRuns(string name)
    {
        // Refused synchronously, before a task exists: nothing of the command line is looked at.
        var error = Assert.Throws<CliModuleException>(() => { _ = CliHost.RunAsync(["validate", _anchor], new VerbModule(name, Verb("fine"))); });

        Assert.Contains("not a valid CLI module name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CliArguments_ReadsOptionsFlagsAndIntegers_WithTheDeclaredValueOptions()
    {
        var arguments = new CliArguments(["check", "--kind", "a:b:1", "flow.yaml", "--limit", "25", "--quiet", "--depth", "-3"], ["--kind", "--depth"]);

        Assert.Equal(["check", "flow.yaml"], arguments.Positionals);
        Assert.Equal("flow.yaml", arguments.Positional(1));
        Assert.Null(arguments.Positional(2));
        Assert.Equal("a:b:1", arguments.GetOption("--kind"));
        Assert.Equal(25, arguments.GetNonNegativeIntOption(10, "--limit"));
        Assert.Equal(10, arguments.GetNonNegativeIntOption(10, "--depth"));
        Assert.True(arguments.HasFlag("--quiet"));
        Assert.False(arguments.HasFlag("--loud"));
        Assert.True(arguments.IsValueOption("--db"));
        Assert.Contains("--db", CliArguments.SqlFlowValueOptions);
        Assert.Throws<ArgumentException>(() => new CliArguments(["check", null!]));
    }

    [Fact]
    public void CliArguments_AValueOptionWithoutAValue_DoesNotSwallowTheNextFlag()
    {
        var arguments = new CliArguments(["check", "--kind", "--json"], ["--kind"]);

        Assert.Null(arguments.GetOption("--kind"));
        Assert.True(arguments.HasFlag("--json"));
    }

    private static CliVerb Verb(string name) => new(name, [$"sqlflow {name}"], _ => Task.FromResult(0));

    /// <summary>What a probe verb saw. The command's provider is disposed when the command ends, so the module service is read while the verb runs.</summary>
    public sealed record ProbeCall(string ModuleName, string Verb, CliArguments Arguments, bool Json, string ServiceDescription);

    public sealed record ProbeService(string Description);

    private sealed class ProbeModule : ICliModule
    {
        public static readonly string[] UsageLines = ["sqlflow probe <target> [--label <l>]", "                                 Probe a target"];

        public List<ProbeCall> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Exception? ConfigureFailure { get; init; }

        public string Name => "probe";

        public IReadOnlyList<CliVerb> Verbs =>
        [
            new CliVerb("probe", UsageLines, context =>
            {
                Calls.Add(new ProbeCall(
                    context.ModuleName, context.Verb, context.Arguments, context.Json,
                    context.Services.GetRequiredService<ProbeService>().Description));
                return Failure is null ? Task.FromResult(7) : Task.FromException<int>(Failure);
            })
            {
                Subcommands = ["inspect"],
                ValueOptions = ["--label"],
                Flags = ["--connect"],
            },
        ];

        public void ConfigureServices(CliModuleServices services)
        {
            if (ConfigureFailure is not null)
            {
                throw ConfigureFailure;
            }

            services.Services.AddSingleton(new ProbeService($"registered by {services.ModuleName} for {services.Scope}"));
        }
    }

    private sealed class VerbModule(string name, params CliVerb[] verbs) : ICliModule
    {
        public string Name => name;

        public IReadOnlyList<CliVerb> Verbs => verbs;

        public void ConfigureServices(CliModuleServices services)
        {
        }
    }
}
