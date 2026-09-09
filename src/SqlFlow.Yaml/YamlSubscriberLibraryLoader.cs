using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Subscribers;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>What one subscriber library file declares: the consumers, the connections their queries run
/// against, and any warnings raised parsing it.</summary>
public sealed record SubscriberLibrary(
    IReadOnlyList<DataSubscriber> Subscribers,
    IReadOnlyDictionary<string, DataSource> Connections,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loads a subscriber library file: a <c>subscribers.yaml</c> (or <c>*.subscribers.yaml</c>) whose top-level
/// <c>subscribers:</c> key maps a consumer's name to what it is and the queries it runs. This is the V3 form of
/// the legacy <c>flw.DataSubscriber</c> + <c>flw.DataSubscriberQuery</c> pair, collapsed into one file per
/// estate because a subscriber is metadata about consumption, not a pipeline: it never runs, never moves data,
/// and has no schedule, so making it a flow document would put a permanently idle pipeline in every run plan.
/// <para>
/// The queries matter as much as the names. Each one is parsed as T-SQL and every table and view it touches
/// becomes a <c>Reads</c> lineage edge attributed to the subscriber's node, which is what turns "we have a
/// list of dashboards" into "this table is consumed by these three reports". A query therefore needs a
/// <c>server:</c>, the connection alias it runs against, so its two-part names resolve to the same node
/// identities the loading flows write; the legacy model carried the same thing as
/// <c>DataSubscriberQuery.srcServer</c>.
/// </para>
/// A malformed entry is dropped with a warning rather than throwing: one bad subscriber must not blind the
/// estate's lineage, exactly as one unparseable flow document does not.
/// </summary>
public sealed class YamlSubscriberLibraryLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private sealed class LibraryYaml
    {
        public Dictionary<string, object>? Connections { get; set; }

        public Dictionary<string, SubscriberYaml?>? Subscribers { get; set; }
    }

    private sealed class SubscriberYaml
    {
        public string? Type { get; set; }

        public string? Owner { get; set; }

        public string? Description { get; set; }

        /// <summary>Remarks about the subscriber's state, kept apart from <see cref="Description"/>.</summary>
        public string? Notes { get; set; }

        public string? Url { get; set; }

        /// <summary>The default connection alias for every query that does not name its own.</summary>
        public string? Server { get; set; }

        public List<QueryYaml?>? Queries { get; set; }
    }

    private sealed class QueryYaml
    {
        public string? Name { get; set; }

        public string? Server { get; set; }

        public string? Sql { get; set; }
    }

    /// <summary>The type recorded for a subscriber that declares none. The legacy column was nullable and a
    /// good many production rows left it empty; dropping those subscribers would lose real consumers, so the
    /// unknown type is carried explicitly instead.</summary>
    public const string UnknownType = "Unknown";

    /// <summary>Parses a subscriber library file's YAML. <paramref name="source"/> only labels warnings.</summary>
    public SubscriberLibrary Parse(string yaml, string source = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var warnings = new List<string>();

        LibraryYaml? parsed;
        try
        {
            parsed = _deserializer.Deserialize<LibraryYaml>(yaml);
        }
        catch (YamlException ex)
        {
            warnings.Add($"{source}: invalid subscriber library - {ex.Message}");
            return new SubscriberLibrary([], new Dictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase), warnings);
        }

        Dictionary<string, DataSource> connections;
        try
        {
            connections = YamlDocumentParts.MapConnections(parsed?.Connections, source);
        }
        catch (FlowValidationException ex)
        {
            warnings.Add($"{source}: {ex.Message}");
            return new SubscriberLibrary([], new Dictionary<string, DataSource>(StringComparer.OrdinalIgnoreCase), warnings);
        }

        if (parsed?.Subscribers is not { Count: > 0 } entries)
        {
            warnings.Add($"{source}: no 'subscribers:' entries; nothing is registered as consuming the warehouse.");
            return new SubscriberLibrary([], connections, warnings);
        }

        var subscribers = new List<DataSubscriber>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawName, entry) in entries)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                warnings.Add($"{source}: a subscriber with a blank name is ignored.");
                continue;
            }

            if (!seen.Add(name))
            {
                warnings.Add($"{source}: subscriber '{name}' is declared more than once; the first wins.");
                continue;
            }

            if (entry is null)
            {
                warnings.Add($"{source}: subscriber '{name}' declares nothing; ignored.");
                continue;
            }

            var type = string.IsNullOrWhiteSpace(entry.Type) ? UnknownType : entry.Type.Trim();
            if (string.IsNullOrWhiteSpace(entry.Type))
            {
                warnings.Add(
                    $"{source}: subscriber '{name}' declares no 'type'; recorded as '{UnknownType}'. Set it to what "
                    + "consumes the data (PowerBI, Tableau, Excel, Notebook, Application) so the catalog can group by tool.");
            }

            var defaultServer = string.IsNullOrWhiteSpace(entry.Server) ? null : entry.Server.Trim();
            var queries = MapQueries(name, entry.Queries, defaultServer, connections, source, warnings);

            if (queries.Count == 0)
            {
                warnings.Add(
                    $"{source}: subscriber '{name}' has no usable queries, so nothing links it to the warehouse; it "
                    + "will show in the catalog as a consumer of nothing.");
            }

            subscribers.Add(new DataSubscriber
            {
                Name = name,
                Type = type,
                Owner = Trimmed(entry.Owner),
                Description = Trimmed(entry.Description),
                Notes = Trimmed(entry.Notes),
                Url = Trimmed(entry.Url),
                Queries = queries,
            });
        }

        return new SubscriberLibrary(subscribers, connections, warnings);
    }

    private static List<SubscriberQuery> MapQueries(
        string subscriber,
        List<QueryYaml?>? block,
        string? defaultServer,
        Dictionary<string, DataSource> connections,
        string source,
        List<string> warnings)
    {
        var queries = new List<SubscriberQuery>();
        if (block is null)
        {
            return queries;
        }

        var ordinal = 0;
        foreach (var query in block)
        {
            ordinal++;
            if (query is null)
            {
                warnings.Add($"{source}: subscriber '{subscriber}' query #{ordinal} is empty; ignored.");
                continue;
            }

            var queryName = string.IsNullOrWhiteSpace(query.Name) ? $"query{ordinal}" : query.Name.Trim();

            if (string.IsNullOrWhiteSpace(query.Sql))
            {
                warnings.Add(
                    $"{source}: subscriber '{subscriber}' query '{queryName}' declares no 'sql', so it can name no "
                    + "tables; ignored.");
                continue;
            }

            var server = string.IsNullOrWhiteSpace(query.Server) ? defaultServer : query.Server.Trim();
            if (string.IsNullOrEmpty(server))
            {
                warnings.Add(
                    $"{source}: subscriber '{subscriber}' query '{queryName}' names no 'server' and the subscriber "
                    + "declares no default; its tables cannot be resolved to a server, so it is ignored.");
                continue;
            }

            if (!connections.ContainsKey(server))
            {
                warnings.Add(
                    $"{source}: subscriber '{subscriber}' query '{queryName}' runs against connection '{server}', "
                    + "which the document's 'connections:' block does not declare; ignored.");
                continue;
            }

            queries.Add(new SubscriberQuery
            {
                Name = queryName,
                Server = server,
                Sql = query.Sql,
            });
        }

        return queries;
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
