using SqlFlow.Core.Export;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Yaml;

namespace SqlFlow.Execution;

/// <summary>Loads any flow document for execution, applying the file-relative path fixups and the secret-hygiene
/// check that both <c>validate</c> and <c>run</c> rely on. This is the one load path the CLI's verbs and the
/// shared <see cref="DocumentExecutor"/> go through, so a batch member loads under exactly the same rules as a
/// directly-invoked flow.</summary>
public static class DocumentLoader
{
    /// <summary>Loads any flow document, applying the file-relative path fixups: a file flow's source location
    /// and an export flow's target path resolve against the document's directory (ingestion and stored-procedure
    /// flows carry no file path, so they pass through unchanged). Every load also runs the secret-hygiene
    /// check, so an embedded credential warns on validate AND run, before it reaches a commit. The warning is
    /// surfaced through <paramref name="onSecretWarning"/>; a null sink loads silently.</summary>
    public static FlowDocument Load(YamlDocumentLoader documents, string file, Action<string>? onSecretWarning = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        var document = documents.LoadFile(file) switch
        {
            FileFlowDocument doc => new FileFlowDocument { Flow = ResolveRelativeLocation(doc.Flow, file) },
            ExportFlowDocument doc => new ExportFlowDocument { Document = ResolveRelativeExportPath(doc.Document, file) },
            TranslateFlowDocument doc => new TranslateFlowDocument { Document = ResolveRelativeTranslatePath(doc.Document, file) },
            var other => other,
        };

        if (onSecretWarning is not null)
        {
            WarnOnEmbeddedSecrets(document, file, onSecretWarning);
        }

        return document;
    }

    /// <summary>The source-control guard: a connection literal that embeds a credential gets a loud warning
    /// naming the canonical alternatives, routed to the supplied sink. The value itself is never echoed.</summary>
    private static void WarnOnEmbeddedSecrets(FlowDocument document, string file, Action<string> onSecretWarning)
    {
        var connections = document switch
        {
            IngestionFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            ExportFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            StoredProcedureFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            HealthCheckFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            SourceControlFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            CalendarFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            TranslateFlowDocument doc => doc.Document.Connections.Select(c => (c.Alias, c.ConnectionRef)),
            FileFlowDocument doc => [("target", doc.Flow.Target.Connection)],
            _ => Enumerable.Empty<(string, string)>(),
        };

        foreach (var (alias, reference) in connections)
        {
            if (SecretHygiene.LooksLikeEmbeddedSecret(reference))
            {
                onSecretWarning(SecretHygiene.Warning(alias, file));
            }
        }
    }

    private static TranslateDocument ResolveRelativeTranslatePath(TranslateDocument document, string file)
    {
        var path = document.Flow.Output.Path;
        if (!path.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(path))
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();
            var resolved = Path.GetFullPath(Path.Combine(baseDir, path));
            document = document with { Flow = document.Flow with { Output = document.Flow.Output with { Path = resolved } } };
        }

        return document;
    }

    private static ExportDocument ResolveRelativeExportPath(ExportDocument document, string file)
    {
        if (document.Flow.TrgPath is { } path
            && !path.Contains("://", StringComparison.Ordinal)
            && !Path.IsPathRooted(path))
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();
            var resolved = Path.GetFullPath(Path.Combine(baseDir, path));
            document = document with { Flow = document.Flow with { TrgPath = resolved } };
        }

        return document;
    }

    /// <summary>Resolves a relative source location against the flow file's own directory, so a sample's
    /// <c>./data/x.json</c> works no matter which directory the command runs from (paths in a config file are
    /// naturally relative to that file). Absolute locations, cloud URIs (any <c>scheme://</c> such as
    /// <c>abfss://</c>, <c>s3://</c>, <c>gs://</c>, <c>https://</c>), and non-file sources are left untouched. Public
    /// because the CLI's flow-only verbs resolve the same way a document load does.</summary>
    public static FlowDefinition ResolveRelativeLocation(FlowDefinition flow, string file)
    {
        if (flow.Source.Location is { } location
            && !location.Contains("://", StringComparison.Ordinal)
            && !Path.IsPathRooted(location))
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory();
            var resolved = Path.GetFullPath(Path.Combine(baseDir, location));
            flow = flow with { Source = flow.Source with { Location = resolved } };
        }

        return flow;
    }
}
