using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Files;

namespace SqlFlow.Yaml;

/// <summary>The <c>output:</c> / <c>outputs:</c> block a flow uses to declare the file(s) it produces, in the same
/// shape a file source declares its selection so one matcher connects producer to consumer. Binding-only DTO
/// (mutable, nullable); shared by every flow type that can declare outputs (inv, cpy, sftp).</summary>
internal sealed class FileOutputYaml
{
    public string? Location { get; set; }
    public string? SrcFile { get; set; }
    public string? SrcPathMask { get; set; }
}

/// <summary>
/// The single mapping from the YAML <c>output:</c>/<c>outputs:</c> declaration to the shared <see cref="FileOutput"/>
/// model, reused by the invoke, copy, and sftp loaders so a declared output is parsed and validated identically
/// everywhere: each entry's <c>location</c> is required (a drop with nowhere to land is meaningless) and its
/// <c>srcPathMask</c> must be a valid regex, so a broken declaration fails at parse rather than silently never
/// matching downstream.
/// </summary>
internal static class FileOutputMapping
{
    /// <summary>Maps the singular <c>output:</c> convenience and the plural <c>outputs:</c> list into one list
    /// (singular first). Returns an empty list when the flow declares neither.</summary>
    public static IReadOnlyList<FileOutput> Map(
        FileOutputYaml? single, List<FileOutputYaml>? many, string section, string source)
    {
        var outputs = new List<FileOutput>();
        if (single is not null)
        {
            outputs.Add(MapOne(single, $"{section}.output", source));
        }

        if (many is not null)
        {
            for (var i = 0; i < many.Count; i++)
            {
                var node = many[i] ?? throw new FlowValidationException(
                    $"{source}: '{section}.outputs[{i}]' must be a map of output fields.");
                outputs.Add(MapOne(node, $"{section}.outputs[{i}]", source));
            }
        }

        return outputs;
    }

    private static FileOutput MapOne(FileOutputYaml node, string section, string source)
    {
        var location = YamlDocumentParts.NullIfBlank(node.Location)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: '{section}.location' is required when a flow declares an output.");

        var mask = YamlDocumentParts.NullIfBlank(node.SrcPathMask)?.Trim();
        if (mask is not null && !IsValidRegex(mask))
        {
            throw new FlowValidationException(
                $"{source}: '{section}.srcPathMask' is not a valid regular expression.");
        }

        return new FileOutput
        {
            Location = location,
            SrcFile = YamlDocumentParts.NullIfBlank(node.SrcFile)?.Trim(),
            SrcPathMask = mask,
        };
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
