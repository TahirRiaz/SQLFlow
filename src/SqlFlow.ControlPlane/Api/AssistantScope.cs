using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The AI assistant surface: requests the GUI chat and Slack assistants make through the SQLFlow MCP server. Both
/// gateways point the MCP connector at <c>?surface=assistant</c>, and the MCP server marks every control-plane call
/// of such a session with <see cref="HeaderName"/>. The caller's own bearer still decides what it may do; the header
/// only narrows what it is shown, so a request that omits it sees nothing more than its token already allows. The
/// model cannot set the header, which is the property that matters: it cannot opt out of the narrowing.
///
/// On this surface the column allow-list governs every text an endpoint returns, not only the structured schema
/// readers: a string that names a table and, in the same string, a column of that table outside the semantic layer
/// is withheld. That covers flow YAML, executed statements, view bodies, stored example SQL, and error messages
/// alike, through one filter rather than one redaction per endpoint.
/// </summary>
public static partial class AssistantScope
{
    /// <summary>The request header marking the assistant surface.</summary>
    public const string HeaderName = "X-SqlFlow-Surface";

    /// <summary>The <see cref="HeaderName"/> value of the assistant surface.</summary>
    public const string AssistantValue = "assistant";

    /// <summary>What a withheld string reads as, so the model sees why the text is missing.</summary>
    public const string WithheldText = "[withheld: names a column outside the semantic layer]";

    /// <summary>The longest identifier a tokenizer match is kept for (SQL Server's sysname is 128).</summary>
    private const int MaxIdentifierLength = 128;

    /// <summary>How many values one IN-list lookup carries.</summary>
    private const int LookupChunk = 1000;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NothingDenied =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="http"/> carries the assistant surface marker.</summary>
    public static bool IsAssistant(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Headers.TryGetValue(HeaderName, out var values)
            && values.Any(v => string.Equals(v?.Trim(), AssistantValue, StringComparison.OrdinalIgnoreCase));
    }

    // A bracket-quoted identifier, a double-quoted identifier, or a bare word. Bare words split on anything that is
    // not a letter, digit, or identifier punctuation, so "dbo.Employee.Ssn", "Employee_Ssn = 1", and a key such as
    // "srv|dw|dbo|employee" all yield their parts.
    [GeneratedRegex(@"\[([^\]\r\n]{1,128})\]|""([^""\r\n]{1,128})""|([\p{L}_][\p{L}\p{N}_$#@]*)", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary>The identifiers in <paramref name="text"/>, case-insensitively distinct.</summary>
    public static IReadOnlySet<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in IdentifierPattern().Matches(text))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value
                : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            if (value.Length is > 0 and <= MaxIdentifierLength)
            {
                tokens.Add(value);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Withholds every string in <paramref name="node"/> that names a column outside the semantic layer.
    /// <paramref name="denied"/> maps an object name to its columns outside the layer (case-insensitive both ways).
    /// A string is withheld when it contains one of those column names AND its object's name appears either in the
    /// string itself or in a string of the same JSON object or array, or of an enclosing one: that is what lets
    /// <c>{ "objectName": "Employee", "columnName": "Ssn" }</c> be caught as well as <c>SELECT Ssn FROM dbo.Employee</c>.
    /// A column name that also exists, allowed, on an unrelated table is withheld too when both tables are named
    /// nearby; the rule errs toward withholding, like the query guard it mirrors. Returns the redacted node (a new
    /// node only when the root itself is a withheld string) and how many strings were withheld.
    /// </summary>
    public static JsonNode? Redact(
        JsonNode? node, IReadOnlyDictionary<string, IReadOnlySet<string>> denied, out int withheld)
    {
        ArgumentNullException.ThrowIfNull(denied);
        withheld = 0;
        if (node is null || denied.Count == 0)
        {
            return node;
        }

        if (TryGetString(node, out var rootText))
        {
            if (NamesDeniedColumn(rootText, Tokenize(rootText), denied))
            {
                withheld = 1;
                return JsonValue.Create(WithheldText);
            }

            return node;
        }

        withheld = Walk(node, new HashSet<string>(StringComparer.OrdinalIgnoreCase), denied);
        return node;
    }

    private static int Walk(JsonNode node, IReadOnlySet<string> inherited, IReadOnlyDictionary<string, IReadOnlySet<string>> denied)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var context = ContextOf(obj.Select(property => property.Value), inherited);
                var withheld = 0;
                foreach (var name in obj.Select(property => property.Key).ToList())
                {
                    var child = obj[name];
                    if (child is null)
                    {
                        continue;
                    }

                    if (TryGetString(child, out var text))
                    {
                        if (NamesDeniedColumn(text, context, denied))
                        {
                            obj[name] = JsonValue.Create(WithheldText);
                            withheld++;
                        }
                    }
                    else
                    {
                        withheld += Walk(child, context, denied);
                    }
                }

                return withheld;
            }

            case JsonArray array:
            {
                var context = ContextOf(array, inherited);
                var withheld = 0;
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is null)
                    {
                        continue;
                    }

                    if (TryGetString(child, out var text))
                    {
                        if (NamesDeniedColumn(text, context, denied))
                        {
                            array[i] = JsonValue.Create(WithheldText);
                            withheld++;
                        }
                    }
                    else
                    {
                        withheld += Walk(child, context, denied);
                    }
                }

                return withheld;
            }

            default:
                return 0;
        }
    }

    /// <summary>The inherited context plus every identifier in the direct string members of one object or array.</summary>
    private static HashSet<string> ContextOf(IEnumerable<JsonNode?> members, IReadOnlySet<string> inherited)
    {
        var context = new HashSet<string>(inherited, StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            if (member is not null && TryGetString(member, out var text))
            {
                context.UnionWith(Tokenize(text));
            }
        }

        return context;
    }

    private static bool NamesDeniedColumn(
        string text, IReadOnlySet<string> context, IReadOnlyDictionary<string, IReadOnlySet<string>> denied)
    {
        var own = new HashSet<string>(Tokenize(text), StringComparer.OrdinalIgnoreCase);
        if (own.Count == 0)
        {
            return false;
        }

        foreach (var (objectName, columns) in denied)
        {
            if ((own.Contains(objectName) || context.Contains(objectName)) && own.Overlaps(columns))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonNode node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            text = s;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static void CollectTokens(JsonNode? node, HashSet<string> tokens)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    CollectTokens(property.Value, tokens);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectTokens(item, tokens);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                tokens.UnionWith(Tokenize(text));
                break;
        }
    }

    /// <summary>
    /// For the identifiers present in a response, the catalogued objects they name and those objects' columns, also
    /// named, that are outside the semantic layer (no allowed policy row). Scoped to the response's own tokens, so a
    /// lookup costs what the response names rather than what the catalog holds.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> LoadDeniedAsync(
        CatalogDbContext db, IReadOnlySet<string> tokens, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0)
        {
            return NothingDenied;
        }

        var tokenChunks = tokens.Chunk(LookupChunk).ToList();
        var nameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in tokenChunks)
        {
            var rows = await db.Objects.AsNoTracking()
                .Where(o => chunk.Contains(o.Name))
                .Select(o => new { o.Key, o.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                nameByKey[row.Key] = row.Name;
            }
        }

        if (nameByKey.Count == 0)
        {
            return NothingDenied;
        }

        var denied = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyChunk in nameByKey.Keys.Chunk(LookupChunk))
        {
            foreach (var tokenChunk in tokenChunks)
            {
                var rows = await db.ObjectColumns.AsNoTracking()
                    .Where(c => keyChunk.Contains(c.ObjectKey) && tokenChunk.Contains(c.Name)
                        && !db.ColumnPolicies.Any(p => p.IsAllowed && p.ObjectKey == c.ObjectKey && p.ColumnName == c.Name))
                    .Select(c => new { c.ObjectKey, c.Name })
                    .ToListAsync(ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var objectName = nameByKey[row.ObjectKey];
                    if (!denied.TryGetValue(objectName, out var columns))
                    {
                        columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        denied[objectName] = columns;
                    }

                    columns.Add(row.Name);
                }
            }
        }

        return denied.ToDictionary(
            pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Redacts one endpoint result for the assistant surface. A successful value result (<c>Ok&lt;T&gt;</c>, a plain
    /// DTO) is serialized with the app's JSON options, redacted, and returned as JSON only when something was
    /// withheld, so an unaffected response is returned exactly as the endpoint produced it. A problem result has its
    /// detail redacted in place. Anything else (a stream, a file, a 201/202 with headers) passes through unchanged.
    /// </summary>
    public static async Task<object?> RedactResultAsync(HttpContext http, object? result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var inner = result is INestedHttpResult nested ? nested.Result : result;
        var db = http.RequestServices.GetRequiredService<CatalogDbContext>();

        switch (inner)
        {
            case ProblemHttpResult problem:
            {
                if (problem.ProblemDetails.Detail is { Length: > 0 } detail)
                {
                    var denied = await LoadDeniedAsync(db, Tokenize(detail), ct).ConfigureAwait(false);
                    Redact(JsonValue.Create(detail), denied, out var withheld);
                    if (withheld > 0)
                    {
                        problem.ProblemDetails.Detail = WithheldText;
                    }
                }

                return result;
            }

            case IValueHttpResult { Value: { } value } when inner is IStatusCodeHttpResult { StatusCode: null or StatusCodes.Status200OK }:
                return await RedactValueAsync(http, db, value, result, ct).ConfigureAwait(false);

            case string text:
            {
                var denied = await LoadDeniedAsync(db, Tokenize(text), ct).ConfigureAwait(false);
                Redact(JsonValue.Create(text), denied, out var withheld);
                return withheld > 0 ? WithheldText : result;
            }

            case null or IResult:
                return result;

            default:
                return await RedactValueAsync(http, db, inner, result, ct).ConfigureAwait(false);
        }
    }

    private static async Task<object?> RedactValueAsync(
        HttpContext http, CatalogDbContext db, object value, object? original, CancellationToken ct)
    {
        var options = http.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options);
        if (node is null)
        {
            return original;
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTokens(node, tokens);
        var denied = await LoadDeniedAsync(db, tokens, ct).ConfigureAwait(false);
        var redacted = Redact(node, denied, out var withheld);
        return withheld == 0 ? original : TypedResults.Json(redacted, options);
    }
}

/// <summary>The endpoint filter applying <see cref="AssistantScope.RedactResultAsync"/> to requests carrying the
/// assistant surface marker; every other request is untouched.</summary>
public sealed class AssistantRedactionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var result = await next(context).ConfigureAwait(false);
        var http = context.HttpContext;
        return AssistantScope.IsAssistant(http)
            ? await AssistantScope.RedactResultAsync(http, result, http.RequestAborted).ConfigureAwait(false)
            : result;
    }
}
