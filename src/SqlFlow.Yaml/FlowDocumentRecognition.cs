using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Yaml;

/// <summary>
/// Tells a YAML file that is meant to be a flow document from one that is not. Discovery is by extension (every
/// <c>*.yaml</c> under an estate is offered to the loader), so a file the loader refuses is either something else
/// entirely (a config file, a companion document, unrelated YAML), which the estate scan leaves out quietly, or a flow
/// that does not load, which it must report: dropping a flow without a word would retire its pipeline and its schedule
/// on the next sync as though it had left the repository.
/// </summary>
public static partial class FlowDocumentRecognition
{
    /// <summary>
    /// Whether the file at <paramref name="path"/>, whose text is <paramref name="yaml"/>, is recognisably a flow
    /// document: its root mapping declares a non-blank <c>flowType</c>, or it declares no <c>documentType</c> (a
    /// companion document is never a flow) and either carries the historical <c>.flow.yaml</c> suffix or has the
    /// <c>source</c> and <c>target</c> a file flow requires. A file whose YAML does not parse is judged by its suffix
    /// and by the same keys written at the start of a line, since a syntax error must not hide a flow either. Anything
    /// else is not a flow.
    /// </summary>
    public static bool IsRecognisableFlow(string path, string yaml)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(yaml);
        var flowSuffix = Path.GetFileName(path).EndsWith(".flow.yaml", StringComparison.OrdinalIgnoreCase);

        YamlMappingNode? root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        }
        catch (Exception ex) when (ex is YamlException || IsScannerFailure(ex))
        {
            if (FlowTypeLine().IsMatch(yaml))
            {
                return true;
            }

            return !DocumentTypeLine().IsMatch(yaml)
                   && (flowSuffix || (SourceLine().IsMatch(yaml) && TargetLine().IsMatch(yaml)));
        }

        if (root is null)
        {
            return flowSuffix;
        }

        if (Scalar(root, "flowType") is { Length: > 0 })
        {
            return true;
        }

        if (Scalar(root, "documentType") is { Length: > 0 })
        {
            return false;
        }

        return flowSuffix
               || (root.Children.ContainsKey(new YamlScalarNode("source"))
                   && root.Children.ContainsKey(new YamlScalarNode("target")));
    }

    /// <summary>Whether <paramref name="exception"/> is YamlDotNet's scanner rejecting the text: read through the
    /// representation model, it reports some malformed input (a flow sequence left open where the text ends) as an
    /// <see cref="InvalidOperationException"/> rather than a <see cref="YamlException"/>. Identified by the assembly that
    /// threw it, so an <see cref="InvalidOperationException"/> from anywhere else is never taken for a syntax error.</summary>
    private static bool IsScannerFailure(Exception exception)
        => exception is InvalidOperationException && string.Equals(exception.Source, "YamlDotNet", StringComparison.Ordinal);

    private static string? Scalar(YamlMappingNode root, string key)
        => root.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value?.Trim()
            : null;

    [GeneratedRegex(@"^flowType\s*:\s*\S", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex FlowTypeLine();

    [GeneratedRegex(@"^documentType\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DocumentTypeLine();

    [GeneratedRegex(@"^source\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SourceLine();

    [GeneratedRegex(@"^target\s*:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TargetLine();
}
