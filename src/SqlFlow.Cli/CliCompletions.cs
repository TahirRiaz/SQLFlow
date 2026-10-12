using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using SqlFlow.Cli.Hosting;

namespace SqlFlow.Cli;

/// <summary>
/// 'sqlflow completions bash|zsh|powershell': prints a completion script for the requested shell to stdout,
/// for the operator to source or install (e.g. <c>source &lt;(sqlflow completions bash)</c>). One registry
/// drives all three generators, so a new verb is added exactly once; a host's module verbs join it with their
/// subcommands and options. Completion covers verbs, their subcommands, and the shared option set; document paths
/// fall through to the shell's file completion.
/// </summary>
internal static class CliCompletions
{
    /// <summary>Verb -> subcommands (empty when the verb takes a file/flag argument instead).</summary>
    private static readonly (string Verb, string[] Subcommands)[] Registry =
    [
        ("validate", []), ("plan", []), ("run", []), ("infer", []), ("discover", []), ("paths", []), ("flatten", []),
        ("catalog", ["databases", "schemas", "tables", "search", "columns", "scaffold", "scaffold-all"]),
        ("detect-unique-key", []), ("healthcheck", []),
        ("lineage", ["objects", "edges", "waves", "script"]),
        ("auth", []), ("db", ["migrate", "sync", "status"]), ("worker", []),
        ("runs", ["list", "show", "trace", "cancel", "local"]),
        ("groups", ["show", "cancel", "rerun"]),
        ("user", ["reset-password"]),
        ("health", []), ("login", []), ("logout", []), ("whoami", []), ("doctor", []),
        ("trigger", []), ("summary", []), ("nodes", []),
        ("schedules", ["list", "show", "create", "pause", "resume", "delete"]),
        ("repos", ["list", "show", "register", "discover", "sync"]),
        ("pipelines", ["list", "show", "columns", "files"]),
        ("datasources", ["list", "test", "databases", "schemas", "objects", "search", "introspect", "detect-unique-key", "tasks", "task", "cancel"]),
        ("search", []),
        ("powerbi", ["extract", "publish", "list", "remove"]),
        ("ai-context", ["list", "show", "approve", "revoke", "export"]),
        ("completions", ["bash", "zsh", "powershell"]),
    ];

    /// <summary>Every SQLFlow verb; a module verb may not take one of these names.</summary>
    internal static readonly FrozenSet<string> BuiltInVerbs = Registry.Select(r => r.Verb).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The options completion offers everywhere. Verb-specific flags stay in --help; completing this
    /// shared core keeps the script small and never stale for the flags people type most.</summary>
    private static readonly string[] CommonOptions =
    [
        "--url", "--token", "--json", "--repo", "--flow", "--scope", "--batch", "--follow", "--preview",
        "--full", "--from", "--to", "--file-pattern", "--source-filter", "--page", "--page-size", "--status", "--kind",
        "--source", "--object", "--ref", "--db", "--out", "--verbose", "--help", "--operation", "--set", "--payload",
    ];

    public static int Print(string[] positional, CliModuleSet modules)
    {
        var shell = positional.Length > 1 ? positional[1].ToLowerInvariant() : string.Empty;
        var script = Script(shell, modules);
        if (script is null)
        {
            Console.Error.WriteLine("ERROR  'completions' supports: bash, zsh, powershell. E.g. source <(sqlflow completions bash)");
            return 1;
        }

        Console.WriteLine(script);
        return 0;
    }

    /// <summary>The completion script for <paramref name="shell"/> (bash, zsh, powershell or pwsh), or null for any other
    /// shell. Module verbs, subcommands and options have been validated to letters, digits and hyphens, so they embed in the
    /// scripts as they are.</summary>
    internal static string? Script(string shell, CliModuleSet modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var registry = Registry
            .Concat(modules.Verbs.Select(v => (v.Verb.Name, v.Verb.Subcommands.ToArray())))
            .ToList();
        var options = CommonOptions
            .Concat(modules.Verbs.SelectMany(v => v.Verb.ValueOptions.Concat(v.Verb.Flags)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return shell switch
        {
            "bash" => Bash(registry, options),
            "zsh" => Zsh(registry, options),
            "powershell" or "pwsh" => PowerShell(registry, options),
            _ => null,
        };
    }

    private static string Verbs(List<(string Verb, string[] Subcommands)> registry) => string.Join(' ', registry.Select(r => r.Verb));

    private static string Bash(List<(string Verb, string[] Subcommands)> registry, List<string> options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# sqlflow bash completion. Install: sqlflow completions bash > /etc/bash_completion.d/sqlflow");
        builder.AppendLine("_sqlflow_completions() {");
        builder.AppendLine("  local cur prev verbs opts");
        builder.AppendLine("  cur=\"${COMP_WORDS[COMP_CWORD]}\"");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  verbs=\"{Verbs(registry)}\"");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  opts=\"{string.Join(' ', options)}\"");
        builder.AppendLine("  if [[ ${COMP_CWORD} -eq 1 ]]; then");
        builder.AppendLine("    COMPREPLY=( $(compgen -W \"${verbs}\" -- \"${cur}\") ); return 0");
        builder.AppendLine("  fi");
        builder.AppendLine("  case \"${COMP_WORDS[1]}\" in");
        foreach (var (verb, subcommands) in registry.Where(r => r.Subcommands.Length > 0))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    {verb})");
            builder.AppendLine("      if [[ ${COMP_CWORD} -eq 2 ]]; then");
            builder.AppendLine(CultureInfo.InvariantCulture, $"        COMPREPLY=( $(compgen -W \"{string.Join(' ', subcommands)}\" -- \"${{cur}}\") ); return 0");
            builder.AppendLine("      fi ;;");
        }

        builder.AppendLine("  esac");
        builder.AppendLine("  if [[ \"${cur}\" == -* ]]; then");
        builder.AppendLine("    COMPREPLY=( $(compgen -W \"${opts}\" -- \"${cur}\") ); return 0");
        builder.AppendLine("  fi");
        builder.AppendLine("  COMPREPLY=( $(compgen -f -- \"${cur}\") )");
        builder.AppendLine("}");
        builder.AppendLine("complete -F _sqlflow_completions sqlflow");
        return builder.ToString();
    }

    private static string Zsh(List<(string Verb, string[] Subcommands)> registry, List<string> options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("#compdef sqlflow");
        builder.AppendLine("# sqlflow zsh completion. Install into a directory on $fpath as _sqlflow.");
        builder.AppendLine("_sqlflow() {");
        builder.AppendLine("  local -a verbs opts");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  verbs=({Verbs(registry)})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  opts=({string.Join(' ', options)})");
        builder.AppendLine("  if (( CURRENT == 2 )); then");
        builder.AppendLine("    _describe 'verb' verbs; return");
        builder.AppendLine("  fi");
        builder.AppendLine("  case $words[2] in");
        foreach (var (verb, subcommands) in registry.Where(r => r.Subcommands.Length > 0))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    {verb})");
            builder.AppendLine("      if (( CURRENT == 3 )); then");
            builder.AppendLine(CultureInfo.InvariantCulture, $"        local -a subs; subs=({string.Join(' ', subcommands)}); _describe 'subcommand' subs; return");
            builder.AppendLine("      fi ;;");
        }

        builder.AppendLine("  esac");
        builder.AppendLine("  if [[ $words[CURRENT] == -* ]]; then");
        builder.AppendLine("    _describe 'option' opts; return");
        builder.AppendLine("  fi");
        builder.AppendLine("  _files");
        builder.AppendLine("}");
        builder.AppendLine("_sqlflow \"$@\"");
        return builder.ToString();
    }

    private static string PowerShell(List<(string Verb, string[] Subcommands)> registry, List<string> options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# sqlflow PowerShell completion. Install: sqlflow completions powershell | Out-String | Invoke-Expression");
        builder.AppendLine("Register-ArgumentCompleter -Native -CommandName sqlflow -ScriptBlock {");
        builder.AppendLine("    param($wordToComplete, $commandAst, $cursorPosition)");
        builder.AppendLine("    $tokens = $commandAst.CommandElements | ForEach-Object { $_.ToString() }");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    $verbs = @({string.Join(", ", registry.Select(r => $"'{r.Verb}'"))})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    $opts = @({string.Join(", ", options.Select(o => $"'{o}'"))})");
        builder.AppendLine("    $subs = @{");
        foreach (var (verb, subcommands) in registry.Where(r => r.Subcommands.Length > 0))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"        '{verb}' = @({string.Join(", ", subcommands.Select(s => $"'{s}'"))})");
        }

        builder.AppendLine("    }");
        builder.AppendLine("    $candidates = if ($tokens.Count -le 1 -or ($tokens.Count -eq 2 -and $wordToComplete)) { $verbs }");
        builder.AppendLine("        elseif ($subs.ContainsKey($tokens[1]) -and ($tokens.Count -eq 2 -or ($tokens.Count -eq 3 -and $wordToComplete))) { $subs[$tokens[1]] }");
        builder.AppendLine("        elseif ($wordToComplete -like '-*') { $opts }");
        builder.AppendLine("        else { @() }");
        builder.AppendLine("    $candidates | Where-Object { $_ -like \"$wordToComplete*\" } | ForEach-Object {");
        builder.AppendLine("        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
