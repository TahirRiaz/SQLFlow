using System.Text.RegularExpressions;
using SqlFlow.Cli.Hosting;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The CLI's option vocabulary. Every option a verb reads is declared, because that is what lets the CLI refuse an
/// option nobody reads: an undeclared option is taken for a flag, and the value after it becomes a positional argument,
/// so a command that asked for something specific quietly does something broader instead. The scan below reads the CLI's
/// own sources, so a verb that starts reading a new option fails this test until the option joins the vocabulary.
/// </summary>
public sealed partial class CliOptionInventoryTests
{
    /// <summary>Option-shaped strings that are not options of this CLI, with why each is there.</summary>
    private static readonly Dictionary<string, string> NotOptions = new(StringComparer.Ordinal)
    {
        ["--no-build"] = "passed through to 'dotnet run' in a usage example, never read here",
        ["--project"] = "the same",
        ["--filter"] = "shown in a 'dotnet test' example",
        ["--force"] = "named in a message about git, not read as an option",
    };

    [Fact]
    public void Every_option_the_cli_reads_is_declared()
    {
        var directory = Path.Combine(RepositoryRoot(), "src", "SqlFlow.Cli");
        Assert.True(Directory.Exists(directory), $"The CLI's sources are missing: {directory}");

        var undeclared = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            scanned++;
            var text = File.ReadAllText(file);
            foreach (Match match in OptionLiteral().Matches(text))
            {
                var option = match.Groups["option"].Value;
                if (CliArguments.SqlFlowOptions.Contains(option) || NotOptions.ContainsKey(option))
                {
                    continue;
                }

                if (!undeclared.TryGetValue(option, out var files))
                {
                    undeclared[option] = files = [];
                }

                var name = Path.GetFileName(file);
                if (!files.Contains(name, StringComparer.Ordinal))
                {
                    files.Add(name);
                }
            }
        }

        Assert.True(scanned > 0, $"No sources were scanned under {directory}.");
        Assert.True(
            undeclared.Count == 0,
            "Every option the CLI reads belongs in CliArguments.BuiltInValueOptions (it takes a value) or "
            + "CliArguments.BuiltInFlags (it takes none), or in this test's NotOptions when it is not an option of this "
            + "CLI at all. Undeclared: "
            + string.Join("; ", undeclared.Select(entry => $"{entry.Key} ({string.Join(", ", entry.Value)})")));
    }

    [Fact]
    public void An_option_read_for_its_value_is_declared_as_taking_one()
    {
        // An option read through one of these consumes the token after it. Declared as a flag instead, that token is
        // read as a positional argument: 'sqlflow worker --drain-seconds 30' would take 30 for the verb's file.
        string[] readers = ["GetOption", "GetOptions", "ParseIntOption", "FindOption", "FindOptions", "GetNonNegativeIntOption"];
        var directory = Path.Combine(RepositoryRoot(), "src", "SqlFlow.Cli");
        var flags = Flags().ToHashSet(StringComparer.Ordinal);
        var undeclared = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("CliArguments.cs", StringComparison.Ordinal) || IsBuildOutput(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var reader in readers)
            {
                foreach (var arguments in Arguments(text, reader))
                {
                    foreach (Match option in OptionLiteral().Matches(arguments))
                    {
                        var name = option.Groups["option"].Value;
                        if (flags.Contains(name) || !CliArguments.SqlFlowValueOptions.Contains(name))
                        {
                            undeclared[name] = Path.GetFileName(file);
                        }
                    }
                }
            }
        }

        Assert.True(
            undeclared.Count == 0,
            "Read for a value and not declared as taking one, so the value after it becomes a positional argument: "
            + string.Join("; ", undeclared.Select(entry => $"{entry.Key} ({entry.Value})"))
            + ". Move each to CliArguments.BuiltInValueOptions.");
    }

    /// <summary>Each argument list of a call to <paramref name="method"/>, counted across nested parentheses.</summary>
    private static IEnumerable<string> Arguments(string text, string method)
    {
        foreach (Match call in Regex.Matches(text, Regex.Escape(method) + @"\s*\(", RegexOptions.CultureInvariant))
        {
            var depth = 1;
            var start = call.Index + call.Length;
            var i = start;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '(')
                {
                    depth++;
                }
                else if (text[i] == ')')
                {
                    depth--;
                }

                i++;
            }

            yield return text[start..Math.Max(start, i - 1)];
        }
    }

    /// <summary>True for a file under a build output folder rather than the sources.</summary>
    private static bool IsBuildOutput(string file)
        => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [Fact]
    public void An_option_is_either_a_value_option_or_a_flag_and_never_both()
    {
        var both = CliArguments.SqlFlowValueOptions.Intersect(Flags(), StringComparer.Ordinal).ToList();

        Assert.True(both.Count == 0, $"Declared as taking a value and as a flag: {string.Join(", ", both)}");
    }

    /// <summary>The flags, read through the whole vocabulary so the test needs no access of its own to the set.</summary>
    private static IEnumerable<string> Flags() => CliArguments.SqlFlowOptions.Except(CliArguments.SqlFlowValueOptions, StringComparer.Ordinal);

    /// <summary>A string literal that looks like an option: <c>"--name"</c> or <c>"-x"</c>, lower-case as the CLI reads them.</summary>
    [GeneratedRegex("\"(?<option>--?[a-z][a-z0-9-]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex OptionLiteral();

    /// <summary>Walks up from the test binary to the directory holding the solution; the scan reads sources, so a
    /// missing root is a broken test rather than a silent pass.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SqlFlow.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate SqlFlow.sln above '{AppContext.BaseDirectory}'; the source scan cannot run.");
    }
}
