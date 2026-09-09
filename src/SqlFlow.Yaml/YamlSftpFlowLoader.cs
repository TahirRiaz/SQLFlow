using SqlFlow.Core;
using SqlFlow.Core.Sftp;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// Loads an SFTP transfer flow (flowType: sftp) from YAML into a validated <see cref="SftpFlow"/>. YamlDotNet handles
/// the grammar; this class maps the parsed document, normalizes the direction enum, and enforces the server and
/// endpoint requirements. Secret material (password, private key, passphrase) is always a <c>${...}</c> reference.
/// </summary>
public sealed class YamlSftpFlowLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public SftpFlow LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public SftpFlow Parse(string yaml, string source = "<inline>")
    {
        SftpDocumentYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<SftpDocumentYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static SftpFlow Map(SftpDocumentYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "an sftp flow", source);
        var serverYaml = y.Server ?? throw new FlowValidationException($"{source}: 'server' is required.");

        return new SftpFlow
        {
            Name = name,
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Direction = ParseEnum(y.Direction, SftpDirection.Download, "direction", source),
            Server = new SftpServer
            {
                Host = Require(serverYaml.Host, "server.host", source),
                Port = serverYaml.Port ?? 22,
                Username = Require(serverYaml.Username, "server.username", source),
                PasswordRef = YamlDocumentParts.NullIfBlank(serverYaml.PasswordRef),
                PrivateKeyRef = YamlDocumentParts.NullIfBlank(serverYaml.PrivateKeyRef),
                PassphraseRef = YamlDocumentParts.NullIfBlank(serverYaml.PassphraseRef),
            },
            Steps = MapSteps(y, source),
            Overwrite = y.Overwrite ?? true,
            PreserveStructure = y.PreserveStructure ?? true,
            SkipUnchanged = y.SkipUnchanged ?? true,
            Outputs = FileOutputMapping.Map(y.Output, y.Outputs, "sftp", source),
        };
    }

    /// <summary>Builds the transfer steps from either the singular top-level <c>local:</c>/<c>remotePath:</c> (one
    /// step) or the plural <c>items:</c> list (one step per entry). Exactly one form is allowed: declaring both, or
    /// neither, fails at parse.</summary>
    private static IReadOnlyList<SftpStep> MapSteps(SftpDocumentYaml y, string source)
    {
        var hasSingle = !string.IsNullOrWhiteSpace(y.Local) || !string.IsNullOrWhiteSpace(y.RemotePath)
            || !string.IsNullOrWhiteSpace(y.Pattern) || y.Recursive is not null || y.ModifiedWithinDays is not null;
        var hasItems = y.Items is { Count: > 0 };

        if (hasSingle && hasItems)
        {
            throw new FlowValidationException(
                $"{source}: declare either a top-level 'local'/'remotePath' or an 'items' list, not both.");
        }

        if (hasItems)
        {
            var steps = new List<SftpStep>(y.Items!.Count);
            for (var i = 0; i < y.Items!.Count; i++)
            {
                var item = y.Items[i] ?? throw new FlowValidationException($"{source}: 'items[{i}]' must be a map.");
                steps.Add(MapStep(item.Local, item.RemotePath, item.Pattern, item.Recursive, item.ModifiedWithinDays, $"items[{i}]", source));
            }

            return steps;
        }

        return [MapStep(y.Local, y.RemotePath, y.Pattern, y.Recursive, y.ModifiedWithinDays, null, source)];
    }

    private static SftpStep MapStep(
        string? local, string? remotePath, string? pattern, bool? recursive, int? modifiedWithinDays, string? section, string source)
    {
        var localField = section is null ? "local" : $"{section}.local";
        return new SftpStep
        {
            Local = Require(local, localField, source),
            RemotePath = string.IsNullOrWhiteSpace(remotePath) ? "." : remotePath!.Trim(),
            Pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern!.Trim(),
            Recursive = recursive ?? true,
            ModifiedWithinDays = modifiedWithinDays ?? 0,
        };
    }

    private static string Require(string? value, string field, string source)
        => string.IsNullOrWhiteSpace(value) ? throw new FlowValidationException($"{source}: '{field}' is required.") : value.Trim();

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback, string field, string source) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetNames<TEnum>())
        {
            if (candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(candidate);
            }
        }

        throw new FlowValidationException(
            $"{source}: '{value}' is not a valid value for '{field}'. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Binding DTOs (mutable, nullable; YamlDotNet only).
// ---------------------------------------------------------------------------------------------------------------

internal sealed class SftpDocumentYaml
{
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Direction { get; set; }
    public SftpServerYaml? Server { get; set; }
    public string? Local { get; set; }
    public string? RemotePath { get; set; }
    public string? Pattern { get; set; }
    public bool? Recursive { get; set; }
    public int? ModifiedWithinDays { get; set; }
    public bool? Overwrite { get; set; }
    public bool? PreserveStructure { get; set; }
    public bool? SkipUnchanged { get; set; }

    /// <summary>Several transfers in one pipeline: one entry per file set. The alternative to the single top-level
    /// local/remotePath.</summary>
    public List<SftpItemYaml>? Items { get; set; }

    /// <summary>A single declared output (convenience for the one-folder case).</summary>
    public FileOutputYaml? Output { get; set; }

    /// <summary>Several declared outputs: one entry per distinct file set the download drops.</summary>
    public List<FileOutputYaml>? Outputs { get; set; }
}

internal sealed class SftpItemYaml
{
    public string? Local { get; set; }
    public string? RemotePath { get; set; }
    public string? Pattern { get; set; }
    public bool? Recursive { get; set; }
    public int? ModifiedWithinDays { get; set; }
}

internal sealed class SftpServerYaml
{
    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? PasswordRef { get; set; }
    public string? PrivateKeyRef { get; set; }
    public string? PassphraseRef { get; set; }
}
