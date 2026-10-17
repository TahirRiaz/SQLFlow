namespace SqlFlow.Core.Secrets;

/// <summary>
/// The canonical local-development secrets file: <c>.sqlflow/env</c>, KEY=VALUE lines, next to the flow
/// documents (or any parent directory). This is how YAML under source control combines with sensitive
/// values: the documents carry only names (<c>${env:...}</c> references or bare-alias conventions), CI and
/// production inject real environment variables, and a developer's values live in this git-ignored file
/// instead of their shell profile or, worse, the YAML. The PROCESS environment always wins over the file
/// (the 12-factor rule), so a CI variable can never be shadowed by a stray local file. Values are never
/// logged; the loader reports only names.
/// </summary>
public static class LocalEnvFile
{
    /// <summary>The file's path relative to a flow directory. The whole <c>.sqlflow/</c> folder belongs in
    /// .gitignore: run artifacts, model state, and this file.</summary>
    public const string RelativePath = ".sqlflow/env";

    /// <summary>
    /// Finds the nearest <c>.sqlflow/env</c> at <paramref name="startDirectory"/> or any parent, applies it
    /// to the process environment (existing variables untouched), and returns the applied variable NAMES and
    /// the file used (empty and null when no file exists). Malformed lines fail loudly with their line
    /// number: a half-loaded secrets file is a debugging trap.
    /// </summary>
    public static (IReadOnlyList<string> Applied, string? File) ApplyNearest(string startDirectory)
    {
        var candidate = FindNearest(startDirectory);
        return candidate is null ? ([], null) : (Apply(candidate), candidate);
    }

    /// <summary>
    /// The nearest <c>.sqlflow/env</c> at <paramref name="startDirectory"/> or any parent, or null when there is none.
    /// </summary>
    public static string? FindNearest(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".sqlflow", "env");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies one env file to the process environment; see <see cref="ApplyNearest"/>. A file with a malformed line
    /// applies nothing: the first such line is reported, by number, and no variable of the file is set.
    /// </summary>
    public static IReadOnlyList<string> Apply(string path)
    {
        var content = Read(path);
        if (content.Problems.Count > 0)
        {
            var first = content.Problems[0];
            throw new SqlFlowException($"{path}({first.Line}): {first.Message}");
        }

        var applied = new List<string>();
        foreach (var entry in content.Entries)
        {
            // The process environment wins: CI, Kubernetes, and the scheduler outrank a local file.
            if (Environment.GetEnvironmentVariable(entry.Name) is null)
            {
                Environment.SetEnvironmentVariable(entry.Name, entry.Value);
                applied.Add(entry.Name);
            }
        }

        return applied;
    }

    /// <summary>
    /// Reads one env file without applying it: every setting in file order (a name set twice appears twice), and every
    /// line <see cref="Apply"/> refuses, each by number. A problem never carries the line's content: a malformed line in
    /// a secrets file may BE the secret, pasted without its KEY=.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
    public static LocalEnvFileContent Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var entries = new List<LocalEnvFileEntry>();
        var problems = new List<LocalEnvFileProblem>();
        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                problems.Add(new LocalEnvFileProblem(i + 1, "expected KEY=VALUE (or a # comment)."));
                continue;
            }

            var name = line[..separator].Trim();
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                problems.Add(new LocalEnvFileProblem(i + 1, $"'{Truncate(name)}' is not a valid environment variable name (letters, digits, '_')."));
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                // Optional surrounding quotes, for values with leading/trailing spaces or '#'.
                value = value[1..^1];
            }

            entries.Add(new LocalEnvFileEntry(name, value, i + 1));
        }

        return new LocalEnvFileContent(entries, problems);
    }

    /// <summary>Name excerpts in errors stay short; values are never echoed anywhere.</summary>
    private static string Truncate(string name) => name.Length <= 40 ? name : name[..37] + "...";
}

/// <summary>One setting of an env file.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="Value">Its value, trimmed and without surrounding quotes.</param>
/// <param name="Line">The line it is on, from 1.</param>
public sealed record LocalEnvFileEntry(string Name, string Value, int Line);

/// <summary>A line of an env file that <see cref="LocalEnvFile.Apply"/> refuses.</summary>
/// <param name="Line">The line, from 1.</param>
/// <param name="Message">What is wrong with it, never its content.</param>
public sealed record LocalEnvFileProblem(int Line, string Message);

/// <summary>What <see cref="LocalEnvFile.Read"/> found in an env file.</summary>
/// <param name="Entries">Every setting, in file order.</param>
/// <param name="Problems">Every line the file's loader refuses, in file order.</param>
public sealed record LocalEnvFileContent(IReadOnlyList<LocalEnvFileEntry> Entries, IReadOnlyList<LocalEnvFileProblem> Problems);
