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
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, ".sqlflow", "env");
            if (File.Exists(candidate))
            {
                return (Apply(candidate), candidate);
            }
        }

        return ([], null);
    }

    /// <summary>Applies one env file to the process environment; see <see cref="ApplyNearest"/>.</summary>
    public static IReadOnlyList<string> Apply(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var applied = new List<string>();
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
                // The line content is deliberately NOT echoed: a malformed line in a secrets file may BE the
                // secret, pasted without its KEY=.
                throw new SqlFlowException($"{path}({i + 1}): expected KEY=VALUE (or a # comment).");
            }

            var name = line[..separator].Trim();
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                throw new SqlFlowException(
                    $"{path}({i + 1}): '{Truncate(name)}' is not a valid environment variable name (letters, digits, '_').");
            }

            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                // Optional surrounding quotes, for values with leading/trailing spaces or '#'.
                value = value[1..^1];
            }

            // The process environment wins: CI, Kubernetes, and the scheduler outrank a local file.
            if (Environment.GetEnvironmentVariable(name) is null)
            {
                Environment.SetEnvironmentVariable(name, value);
                applied.Add(name);
            }
        }

        return applied;
    }

    /// <summary>Name excerpts in errors stay short; values are never echoed anywhere.</summary>
    private static string Truncate(string name) => name.Length <= 40 ? name : name[..37] + "...";
}
