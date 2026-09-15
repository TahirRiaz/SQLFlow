using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Hosting;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Cli.Hosting;

/// <summary>A module verb as the CLI dispatches it: the verb, its module, and the value-taking options its command line is parsed with.</summary>
internal sealed record CliModuleVerb(ICliModule Module, CliVerb Verb, FrozenSet<string> ValueOptions);

/// <summary>
/// The modules (and branding) a CLI host was started with, validated once: names, verbs, usage lines, subcommands and options. Everything
/// the CLI does with a module goes through here: finding a verb, parsing its command line, registering module services,
/// running a verb, and listing verbs for help and shell completion.
/// </summary>
internal sealed class CliModuleSet
{
    /// <summary>The longest verb or subcommand name a module may declare.</summary>
    public const int MaxVerbLength = 32;

    /// <summary>Flags every verb shares, read before any verb runs; a module may not declare one as taking a value.</summary>
    private static readonly FrozenSet<string> SharedFlags = new[] { "-v", "--verbose", "-h", "--help", "--json" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, CliModuleVerb> _verbs;
    private readonly string[] _moduleValueOptions;
    private readonly FrozenSet<string> _allValueOptions;

    /// <param name="modules">The host's modules.</param>
    /// <param name="branding">The host's branding; SQLFlow's when null.</param>
    /// <exception cref="CliModuleException">A module or one of its verbs breaks a rule; the message names the module.</exception>
    public CliModuleSet(IReadOnlyList<ICliModule> modules, ProductBranding? branding = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Branding = branding ?? ProductBranding.SqlFlow;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var verbs = new Dictionary<string, CliModuleVerb>(StringComparer.Ordinal);
        var ordered = new List<CliModuleVerb>();
        var moduleValueOptions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules)
        {
            if (module is null)
            {
                throw new ArgumentException("A CLI module is null.", nameof(modules));
            }

            var name = module.Name;
            if (!HostModuleNames.IsValid(name))
            {
                throw new CliModuleException(
                    name ?? string.Empty,
                    $"'{name}' (module type '{module.GetType().FullName}') is not a valid CLI module name: use {HostModuleNames.Rule}.");
            }

            if (!names.Add(name))
            {
                throw new CliModuleException(name, $"CLI module '{name}' is registered twice; module names must be unique within a host.");
            }

            var declared = module.Verbs
                ?? throw new CliModuleException(name, $"CLI module '{name}' returned no verb list; return an empty list for a module without verbs.");
            foreach (var verb in declared)
            {
                if (verb is null)
                {
                    throw new CliModuleException(name, $"CLI module '{name}' lists a null verb.");
                }

                Validate(name, verb);
                if (CliCompletions.BuiltInVerbs.Contains(verb.Name))
                {
                    throw new CliModuleException(name, $"CLI module '{name}' adds the verb '{verb.Name}', which is a SQLFlow verb.");
                }

                if (verbs.TryGetValue(verb.Name, out var existing))
                {
                    throw new CliModuleException(
                        name, $"CLI module '{name}' adds the verb '{verb.Name}', which module '{existing.Module.Name}' already adds.");
                }

                var valueOptions = new HashSet<string>(CliArguments.BuiltInValueOptions, StringComparer.Ordinal);
                valueOptions.UnionWith(verb.ValueOptions);
                var entry = new CliModuleVerb(module, verb, valueOptions.ToFrozenSet(StringComparer.Ordinal));
                verbs.Add(verb.Name, entry);
                ordered.Add(entry);
                moduleValueOptions.UnionWith(verb.ValueOptions);
            }
        }

        Modules = [.. modules];
        Verbs = ordered;
        _verbs = verbs.ToFrozenDictionary(StringComparer.Ordinal);
        _moduleValueOptions = [.. moduleValueOptions];
        var all = new HashSet<string>(CliArguments.BuiltInValueOptions, StringComparer.Ordinal);
        all.UnionWith(moduleValueOptions);
        _allValueOptions = all.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>The CLI without modules.</summary>
    public static CliModuleSet Empty { get; } = new([]);

    /// <summary>The host's branding: the help banner's tagline, and a service in every provider the CLI builds.</summary>
    public ProductBranding Branding { get; }

    /// <summary>The modules, in the order the host passed them.</summary>
    public IReadOnlyList<ICliModule> Modules { get; }

    /// <summary>Every module verb, in module order and then declaration order.</summary>
    public IReadOnlyList<CliModuleVerb> Verbs { get; }

    /// <summary>The module verb named <paramref name="command"/>, or null for a SQLFlow verb or an unknown command.</summary>
    public CliModuleVerb? Find(string command) => _verbs.GetValueOrDefault(command);

    /// <summary>
    /// The positional arguments of a command line. The verb is found with every known value-taking option, then the line is
    /// parsed again with exactly the verb's own set: SQLFlow's options for a SQLFlow verb (so modules never change how a
    /// SQLFlow verb parses), and SQLFlow's plus the verb's for a module verb.
    /// </summary>
    public string[] PositionalArguments(string[] args)
    {
        if (_verbs.Count == 0)
        {
            return CliArguments.ParsePositionals(args, CliArguments.BuiltInValueOptions);
        }

        var candidates = CliArguments.ParsePositionals(args, _allValueOptions);
        var command = candidates.Length > 0 ? candidates[0].ToLowerInvariant() : string.Empty;
        return CliArguments.ParsePositionals(args, Find(command)?.ValueOptions ?? CliArguments.BuiltInValueOptions);
    }

    /// <summary>Runs every module's service registration against <paramref name="services"/>, in module order.</summary>
    /// <exception cref="CliModuleException">A module's registration failed; the message names the module.</exception>
    public void ConfigureServices(IServiceCollection services, string[] args, CliServiceScope scope)
    {
        if (Modules.Count == 0)
        {
            return;
        }

        var arguments = new CliArguments(args, _moduleValueOptions);
        foreach (var module in Modules)
        {
            try
            {
                module.ConfigureServices(new CliModuleServices(module.Name, services, arguments, scope));
            }
            catch (Exception ex) when (ex is not CliModuleException)
            {
                throw new CliModuleException(module.Name, $"CLI module '{module.Name}' failed to configure its services: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Runs a module verb. The first Ctrl+C cancels the verb's token (exit 130 when the verb stops on it); a second is left to
    /// end the process. A control plane that cannot be reached is reported on one line (exit 1). A
    /// <see cref="Core.SqlFlowException"/> propagates to the CLI's own error line, as it does for SQLFlow's verbs.
    /// </summary>
    public static async Task<int> InvokeAsync(CliModuleVerb verb, IServiceProvider services, string[] args, bool verbose)
    {
        using var cancel = new CancellationTokenSource();
        var interrupts = 0;
        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            if (Interlocked.Increment(ref interrupts) == 1)
            {
                e.Cancel = true;
                cancel.Cancel();
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            var context = new CliVerbContext(verb.Module.Name, verb.Verb, new CliArguments(args, verb.Verb.ValueOptions), services, verbose, cancel.Token);
            var running = verb.Verb.Handler(context)
                ?? throw new CliModuleException(verb.Module.Name, $"The '{verb.Verb.Name}' verb of CLI module '{verb.Module.Name}' returned no task.");
            return await running.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("CANCELLED  interrupted (Ctrl+C).");
            return 130;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"ERROR  {verb.Verb.Name}: the control plane could not be reached: {SecretHygiene.RedactedMessage(ex)}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    /// <summary>Appends every module verb's usage lines, indented like SQLFlow's own, to the CLI's help.</summary>
    public void AppendUsage(StringBuilder usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        foreach (var entry in Verbs)
        {
            foreach (var line in entry.Verb.Usage)
            {
                usage.Append("  ").AppendLine(line);
            }
        }
    }

    private static void Validate(string module, CliVerb verb)
    {
        if (!IsName(verb.Name))
        {
            throw new CliModuleException(
                module,
                $"CLI module '{module}' adds the verb '{verb.Name}': a verb is lowercase letters, digits and hyphens, starting with a letter, at most {MaxVerbLength} characters.");
        }

        if (verb.Usage.Count == 0 || verb.Usage.Any(line => string.IsNullOrWhiteSpace(line) || line.Contains('\n', StringComparison.Ordinal) || line.Contains('\r', StringComparison.Ordinal)))
        {
            throw new CliModuleException(module, $"The '{verb.Name}' verb of CLI module '{module}' needs at least one usage line, each non-blank and on a single line.");
        }

        var subcommands = verb.Subcommands
            ?? throw new CliModuleException(module, $"The '{verb.Name}' verb of CLI module '{module}' has a null subcommand list.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subcommand in subcommands)
        {
            if (!IsName(subcommand) || !seen.Add(subcommand))
            {
                throw new CliModuleException(
                    module,
                    $"The '{verb.Name}' verb of CLI module '{module}' declares the subcommand '{subcommand}': subcommands are unique, lowercase letters, digits and hyphens, starting with a letter, at most {MaxVerbLength} characters.");
            }
        }

        var valueOptions = verb.ValueOptions
            ?? throw new CliModuleException(module, $"The '{verb.Name}' verb of CLI module '{module}' has a null value option list.");
        var flags = verb.Flags
            ?? throw new CliModuleException(module, $"The '{verb.Name}' verb of CLI module '{module}' has a null flag list.");
        foreach (var option in valueOptions.Concat(flags))
        {
            if (!IsOption(option))
            {
                throw new CliModuleException(
                    module,
                    $"The '{verb.Name}' verb of CLI module '{module}' declares the option '{option}': an option is '-' and one letter or digit, or '--' and letters, digits and hyphens starting with a letter or digit.");
            }
        }

        foreach (var option in valueOptions)
        {
            if (SharedFlags.Contains(option))
            {
                throw new CliModuleException(
                    module, $"The '{verb.Name}' verb of CLI module '{module}' declares '{option}' as taking a value, but it is a flag every verb shares.");
            }

            if (flags.Contains(option, StringComparer.Ordinal))
            {
                throw new CliModuleException(
                    module, $"The '{verb.Name}' verb of CLI module '{module}' declares '{option}' both as taking a value and as a flag.");
            }
        }

        foreach (var flag in flags)
        {
            if (CliArguments.BuiltInValueOptions.Contains(flag))
            {
                throw new CliModuleException(
                    module, $"The '{verb.Name}' verb of CLI module '{module}' declares '{flag}' as a flag, but SQLFlow reads it as taking a value.");
            }
        }
    }

    private static bool IsName(string? value)
        => value is { Length: > 0 and <= MaxVerbLength }
           && char.IsAsciiLetterLower(value[0])
           && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static bool IsOption(string? value)
    {
        if (value is null || value.Length is < 2 or > 64 || value[0] != '-')
        {
            return false;
        }

        if (value[1] != '-')
        {
            return value.Length == 2 && char.IsAsciiLetterOrDigit(value[1]);
        }

        return value.Length > 2
               && char.IsAsciiLetterOrDigit(value[2])
               && value.Skip(2).All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
