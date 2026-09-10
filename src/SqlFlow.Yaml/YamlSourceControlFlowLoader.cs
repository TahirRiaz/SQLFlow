using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.SourceControl;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed source-control document (flowType: scm): the flow itself plus the document-local connection
/// registry it declares. The connection becomes an in-memory data-source store so the database resolves through
/// the exact same secretless pipeline as every other flow, with no control database anywhere.
/// </summary>
public sealed record SourceControlDocument
{
    public required SourceControlFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on <c>source</c>).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }
}

/// <summary>
/// Loads a source-control flow (flowType: scm) from YAML. YamlDotNet handles the grammar; this class maps and
/// validates the parsed document into a <see cref="SourceControlDocument"/>. The database to script is a
/// declared connection (SQL Server only, since SMO scripts SQL Server); the repository's remote and git
/// credentials are <c>${...}</c> references, never literals, so nothing secret lands in the versioned YAML.
/// </summary>
public sealed class YamlSourceControlFlowLoader
{
    private const string SourceConnectionName = "source";

    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public SourceControlDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public SourceControlDocument Parse(string yaml, string source = "<inline>")
    {
        SourceControlYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<SourceControlYaml>(yaml);
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

    private static SourceControlDocument Map(SourceControlYaml y, string source)
    {
        var name = YamlDocumentParts.RequireFlowName(y.Name, "a source-control flow (flowType: scm)", source);

        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var server = YamlDocumentParts.ResolveEndpointConnection(
            sourceYaml.Server, sourceYaml.Connection, sourceYaml.Provider, "source", SourceConnectionName, connections, source);

        // SMO scripts SQL Server; a foreign-provider source is a configuration error caught at parse time.
        YamlDocumentParts.RequireSqlServerConnection(connections, server, "source", "a source-control flow's source", source);

        var repository = MapRepository(y.Repository, source);
        var scripting = MapScripting(y.Scripting, source);

        var flow = new SourceControlFlow
        {
            FlowId = YamlDocumentParts.StableFlowId(name),
            SysAlias = name,
            Description = YamlDocumentParts.NullIfBlank(y.Description),
            Batch = YamlDocumentParts.NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Server = server,
            Database = YamlDocumentParts.NullIfBlank(sourceYaml.Database),
            Repository = repository,
            Scripting = scripting,
        };

        return new SourceControlDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
        };
    }

    private static SourceControlRepository MapRepository(SourceControlRepositoryYaml? y, string source)
    {
        if (y is null)
        {
            throw new FlowValidationException($"{source}: 'repository' is required (at least 'repository.path').");
        }

        var path = YamlDocumentParts.NullIfBlank(y.Path)
            ?? throw new FlowValidationException($"{source}: 'repository.path' is required (the local git working directory).");

        var remote = YamlDocumentParts.NullIfBlank(y.Remote);
        var username = YamlDocumentParts.NullIfBlank(y.Username);
        var secret = YamlDocumentParts.NullIfBlank(y.Secret);

        // The secretless contract: a literal git secret in a versioned document is the exact thing this feature
        // exists to prevent. Require a ${...} reference (env or key vault), the same rule connection strings follow.
        RequireReferenceNotLiteral(secret, "repository.secret", source);
        RequireReferenceNotLiteral(username, "repository.username", source);

        if (remote is not null && secret is null)
        {
            throw new FlowValidationException(
                $"{source}: 'repository.remote' is set but 'repository.secret' is not. Pushing needs a credential; " +
                "set 'repository.secret' to a ${env:...} reference (a BitBucket app password or a GitHub token), or " +
                "remove 'repository.remote' to keep the history local.");
        }

        var branch = YamlDocumentParts.NullIfBlank(y.Branch) ?? "main";
        var author = y.Author;

        return new SourceControlRepository
        {
            WorkingDirectory = path,
            Remote = remote,
            Branch = branch,
            Username = username,
            Secret = secret,
            AuthorName = YamlDocumentParts.NullIfBlank(author?.Name) ?? "SQLFlow",
            AuthorEmail = YamlDocumentParts.NullIfBlank(author?.Email) ?? "sqlflow@localhost",
        };
    }

    private static SourceControlScripting MapScripting(SourceControlScriptingYaml? y, string source)
    {
        if (y is null)
        {
            return new SourceControlScripting();
        }

        var dataTables = NormalizeDataTables(y.Data, source);
        var include = ValidateTypes(y.Include, "scripting.include", source);
        var exclude = ValidateTypes(y.Exclude, "scripting.exclude", source);

        var scripting = new SourceControlScripting
        {
            DataTables = dataTables,
            IncludeTypes = include,
            ExcludeTypes = exclude,
            Parallelism = ValidateParallelism(y.Parallelism, source),
        };

        // An absent 'excludeSchemas' keeps the record's default (the engine's staging schema); an authored one
        // replaces it outright, including an explicitly empty list, which is how a flow asks for the staging
        // schema to be versioned after all.
        return y.ExcludeSchemas is null
            ? scripting
            : scripting with { ExcludeSchemas = NormalizeSchemas(y.ExcludeSchemas) };
    }

    /// <summary>The lane count the scripter fans out over, defaulted when absent and bounded when authored: a
    /// zero or negative count would script nothing, and an unbounded one would open as many connections to a
    /// production server as the author happened to type.</summary>
    private static int ValidateParallelism(int? value, string source)
    {
        if (value is not { } lanes)
        {
            return SourceControlScripting.DefaultParallelism;
        }

        if (lanes < 1 || lanes > SourceControlScripting.MaximumParallelism)
        {
            throw new FlowValidationException(
                $"{source}: 'scripting.parallelism' must be between 1 and " +
                $"{SourceControlScripting.MaximumParallelism}, but was {lanes}.");
        }

        return lanes;
    }

    /// <summary>Normalizes the excluded-schema list: blanks dropped, each name trimmed and unbracketed, and
    /// duplicates removed case-insensitively (the scripter compares schema names case-insensitively too).</summary>
    private static IReadOnlyList<string> NormalizeSchemas(List<string> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(items.Count);
        foreach (var raw in items)
        {
            var value = YamlDocumentParts.NullIfBlank(raw);
            if (value is null)
            {
                continue;
            }

            // A bracketed [raw] is the same schema as raw; strip a single surrounding pair, as data tables do.
            var name = SplitQualifiedName(value)[^1];
            if (name.Length > 0 && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>Normalizes each data-table entry to a canonical <c>schema.table</c> form (the rightmost two
    /// parts of a 1-to-3-part name), de-duplicated case-insensitively, matching how the scripter compares them.</summary>
    private static IReadOnlyList<string> NormalizeDataTables(List<string>? items, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in items)
        {
            var value = YamlDocumentParts.NullIfBlank(raw);
            if (value is null)
            {
                continue;
            }

            var parts = SplitQualifiedName(value).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0)
            {
                throw new FlowValidationException($"{source}: 'scripting.data' has an empty table name.");
            }

            var name = parts[^1];
            var schema = parts.Count >= 2 ? parts[^2] : "dbo";
            var canonical = $"{schema}.{name}";
            if (seen.Add(canonical))
            {
                result.Add(canonical);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ValidateTypes(List<string>? items, string field, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var result = new List<string>(items.Count);
        foreach (var raw in items)
        {
            var value = YamlDocumentParts.NullIfBlank(raw);
            if (value is null)
            {
                continue;
            }

            if (!SourceControlObjectTypes.IsKnown(value))
            {
                throw new FlowValidationException(
                    $"{source}: '{field}' has unknown object type '{value}'. Allowed: {string.Join(", ", SourceControlObjectTypes.All)}.");
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>Splits a (possibly bracketed) qualified name on dots that sit outside <c>[...]</c>, stripping a
    /// single surrounding bracket pair from each part. Tolerant of the simple <c>schema.table</c> and bracketed
    /// <c>[schema].[table]</c> forms a data-table entry takes.</summary>
    private static List<string> SplitQualifiedName(string value)
    {
        var parts = new List<string>();
        var token = new System.Text.StringBuilder();
        var inBracket = false;
        foreach (var c in value)
        {
            switch (c)
            {
                case '[' when !inBracket:
                    inBracket = true;
                    break;
                case ']' when inBracket:
                    inBracket = false;
                    break;
                case '.' when !inBracket:
                    parts.Add(token.ToString().Trim());
                    token.Clear();
                    break;
                default:
                    token.Append(c);
                    break;
            }
        }

        parts.Add(token.ToString().Trim());
        return parts;
    }

    private static void RequireReferenceNotLiteral(string? value, string field, string source)
    {
        if (value is null)
        {
            return;
        }

        // A whole ${scheme:locator} reference is the only accepted non-null form; anything else is a literal.
        var trimmed = value.Trim();
        var isReference = trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}');
        if (!isReference)
        {
            throw new FlowValidationException(
                $"{source}: '{field}' must be a ${{env:NAME}} or ${{keyvault:vault/secret}} reference, never a literal. " +
                "Put the value in the git-ignored .sqlflow/env file or your secret store.");
        }
    }

    private sealed class SourceControlYaml
    {
        public string? FlowType { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Batch { get; set; }
        public string? Lifecycle { get; set; }
        public Dictionary<string, object>? Connections { get; set; }
        public SourceControlSourceYaml? Source { get; set; }
        public SourceControlRepositoryYaml? Repository { get; set; }
        public SourceControlScriptingYaml? Scripting { get; set; }
    }

    private sealed class SourceControlSourceYaml
    {
        public string? Server { get; set; }
        public string? Connection { get; set; }
        public string? Provider { get; set; }
        public string? Database { get; set; }
    }

    private sealed class SourceControlRepositoryYaml
    {
        public string? Path { get; set; }
        public string? Remote { get; set; }
        public string? Branch { get; set; }
        public string? Username { get; set; }
        public string? Secret { get; set; }
        public SourceControlAuthorYaml? Author { get; set; }
    }

    private sealed class SourceControlAuthorYaml
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
    }

    private sealed class SourceControlScriptingYaml
    {
        public List<string>? Data { get; set; }
        public List<string>? Include { get; set; }
        public List<string>? Exclude { get; set; }
        public List<string>? ExcludeSchemas { get; set; }
        public int? Parallelism { get; set; }
    }
}
