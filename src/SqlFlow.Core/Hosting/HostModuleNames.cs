namespace SqlFlow.Core.Hosting;

/// <summary>
/// The one rule every host module name follows, whichever host the module plugs into (control plane, CLI, a module
/// database): lowercase ASCII letters, digits and hyphens, starting with a letter, at most <see cref="MaxLength"/>
/// characters. A name that follows it is safe in log lines, configuration keys, file names and shell scripts.
/// </summary>
public static class HostModuleNames
{
    /// <summary>The longest name a module may have.</summary>
    public const int MaxLength = 64;

    /// <summary>The rule in words, for error messages.</summary>
    public const string Rule = "lowercase letters, digits and hyphens, starting with a letter, at most 64 characters";

    /// <summary>True when <paramref name="name"/> follows the rule.</summary>
    public static bool IsValid(string? name)
        => name is { Length: > 0 and <= MaxLength }
           && char.IsAsciiLetterLower(name[0])
           && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
