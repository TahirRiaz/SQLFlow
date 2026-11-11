using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SqlFlow.Tests.Dataverse;

/// <summary>One column of the fake table, as its attribute metadata describes it.</summary>
internal sealed record FakeAttribute(string LogicalName, string TypeName)
{
    public bool IsValidForRead { get; init; } = true;

    public bool IsValidODataAttribute { get; init; } = true;

    public string? AttributeOf { get; init; }

    public int? MaxLength { get; init; }

    public int? DatabaseLength { get; init; }

    public string? DateTimeBehavior { get; init; }

    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>The older <c>AttributeType</c> value: the type name without its suffix, Virtual for the virtual kinds.</summary>
    public string AttributeType => TypeName switch
    {
        "MultiSelectPicklistType" or "ImageType" or "FileType" or "VirtualType" => "Virtual",
        _ => TypeName[..^"Type".Length],
    };

    public bool IsLookup => TypeName is "LookupType" or "CustomerType" or "OwnerType";

    /// <summary>The property a row carries the value in.</summary>
    public string Property => IsLookup ? $"_{LogicalName}_value" : LogicalName;
}

/// <summary>A captured request: its URL and the headers the tests assert on.</summary>
internal sealed record FakeRequest(Uri Url, string? Prefer, string? Authorization, string? ODataVersion);

/// <summary>
/// A scripted Dataverse environment behind an <see cref="HttpMessageHandler"/>: it answers the metadata requests (the
/// table definition, its attributes and their type casts) and the row requests the reader makes, honouring <c>$select</c>,
/// the <c>$filter</c> forms the reader writes (a watermark bound and simple authored comparisons joined by <c>and</c>),
/// <c>$orderby</c>, the <c>odata.maxpagesize</c> preference with <c>@odata.nextLink</c> paging, and annotations. Tests change
/// its columns and rows between runs the way a maker changes a Power Apps table, and script faults ahead of requests.
/// </summary>
internal sealed partial class FakeDataverse : HttpMessageHandler
{
    public const string Host = "contoso.crm4.dynamics.com";
    public const string Environment = "https://" + Host;

    private readonly ConcurrentQueue<FakeRequest> _requests = new();
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage?>> _faults = new();

    public string Table { get; set; } = "account";

    public string EntitySet { get; set; } = "accounts";

    public string PrimaryId { get; set; } = "accountid";

    public List<FakeAttribute> Attributes { get; } = [];

    /// <summary>The rows, by logical name. A lookup's value is its id; <c>&lt;column&gt;@label</c> and <c>&lt;column&gt;@type</c>
    /// keys hold the formatted value and the lookup's table the annotations report.</summary>
    public List<Dictionary<string, object?>> Rows { get; } = [];

    /// <summary>The bearer token the environment accepts; any other is answered 401.</summary>
    public string AcceptedToken { get; set; } = "token-1";

    /// <summary>Rewrites a page's next link before it is sent (a foreign host, a link back to itself).</summary>
    public Func<string, string>? NextLinkRewrite { get; set; }

    public IReadOnlyList<FakeRequest> Requests => _requests.ToList();

    public IReadOnlyList<FakeRequest> RowRequests => Requests.Where(r => !r.Url.AbsolutePath.Contains("EntityDefinitions", StringComparison.Ordinal)).ToList();

    /// <summary>Answers the next request that <paramref name="when"/> matches with the given status instead of serving it.</summary>
    public void Fail(Func<Uri, bool> when, HttpStatusCode status, string body = "", string? retryAfter = null, int times = 1)
    {
        var remaining = times;
        _faults.Enqueue(request =>
        {
            if (remaining <= 0 || !when(request.RequestUri!))
            {
                return null;
            }

            remaining--;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            }

            return response;
        });
    }

    public static bool IsRowRequest(Uri url) => !url.AbsolutePath.Contains("EntityDefinitions", StringComparison.Ordinal);

    public static string ODataError(string message, string code = "0x80040217")
        => JsonSerializer.Serialize(new { error = new { code, message } });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!;
        _requests.Enqueue(new FakeRequest(
            url,
            Header(request, "Prefer"),
            request.Headers.Authorization?.ToString(),
            Header(request, "OData-Version")));

        foreach (var fault in _faults)
        {
            if (fault(request) is { } scripted)
            {
                return Task.FromResult(scripted);
            }
        }

        if (!string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The fake environment was asked for another host: {url}");
        }

        if (request.Headers.Authorization?.Parameter != AcceptedToken)
        {
            return Task.FromResult(Json(HttpStatusCode.Unauthorized, ODataError("The user is not authenticated.", "0x80072560")));
        }

        var path = Uri.UnescapeDataString(url.AbsolutePath);
        const string root = "/api/data/v9.2/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return Task.FromResult(Json(HttpStatusCode.NotFound, ODataError($"Resource not found for the segment '{path}'.")));
        }

        var segment = path[root.Length..];
        var query = Query(url);
        return Task.FromResult(segment.StartsWith("EntityDefinitions(", StringComparison.Ordinal)
            ? Metadata(segment)
            : segment == EntitySet
                ? Page(url, query, Header(request, "Prefer") ?? string.Empty)
                : Json(HttpStatusCode.NotFound, ODataError($"Resource not found for the segment '{segment}'.")));
    }

    private HttpResponseMessage Metadata(string segment)
    {
        var match = DefinitionPattern().Match(segment);
        if (!match.Success || match.Groups["table"].Value != Table)
        {
            return Json(HttpStatusCode.NotFound, ODataError($"Could not find an entity with name {match.Groups["table"].Value}.", "0x80040217"));
        }

        var rest = match.Groups["rest"].Value;
        if (rest.Length == 0)
        {
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { LogicalName = Table, EntitySetName = EntitySet, PrimaryIdAttribute = PrimaryId }));
        }

        IEnumerable<JsonObject> items = rest switch
        {
            "/Attributes" => Attributes.Select(a => new JsonObject
            {
                ["LogicalName"] = a.LogicalName,
                ["AttributeType"] = a.AttributeType,
                ["AttributeTypeName"] = new JsonObject { ["Value"] = a.TypeName },
                ["IsValidForRead"] = a.IsValidForRead,
                ["IsValidODataAttribute"] = a.IsValidODataAttribute,
                ["AttributeOf"] = a.AttributeOf,
            }),
            "/Attributes/Microsoft.Dynamics.CRM.StringAttributeMetadata" => Attributes.Where(a => a.TypeName == "StringType").Select(a => new JsonObject
            {
                ["LogicalName"] = a.LogicalName,
                ["MaxLength"] = a.MaxLength,
                ["DatabaseLength"] = a.DatabaseLength,
            }),
            "/Attributes/Microsoft.Dynamics.CRM.MemoAttributeMetadata" => Attributes.Where(a => a.TypeName == "MemoType").Select(a => new JsonObject
            {
                ["LogicalName"] = a.LogicalName,
                ["MaxLength"] = a.MaxLength,
            }),
            "/Attributes/Microsoft.Dynamics.CRM.DateTimeAttributeMetadata" => Attributes.Where(a => a.TypeName == "DateTimeType").Select(a => new JsonObject
            {
                ["LogicalName"] = a.LogicalName,
                ["DateTimeBehavior"] = a.DateTimeBehavior is null ? null : new JsonObject { ["Value"] = a.DateTimeBehavior },
            }),
            "/Attributes/Microsoft.Dynamics.CRM.LookupAttributeMetadata" => Attributes.Where(a => a.IsLookup).Select(a => new JsonObject
            {
                ["LogicalName"] = a.LogicalName,
                ["Targets"] = new JsonArray(a.Targets.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            }),
            _ => throw new InvalidOperationException($"The fake environment has no metadata answer for '{rest}'."),
        };

        return Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(items.Cast<JsonNode?>().ToArray()) }.ToJsonString());
    }

    private HttpResponseMessage Page(Uri url, Dictionary<string, string> query, string prefer)
    {
        var byName = Attributes.ToDictionary(a => a.LogicalName, StringComparer.Ordinal);
        IEnumerable<Dictionary<string, object?>> rows = Rows;

        if (query.TryGetValue("$filter", out var filter))
        {
            var conditions = Conditions(filter);
            rows = rows.Where(row => conditions.All(c => c(row)));
        }

        if (query.TryGetValue("$orderby", out var orderBy))
        {
            IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
            foreach (var term in orderBy.Split(','))
            {
                var name = term.Trim().Split(' ')[0];
                ordered = ordered is null
                    ? rows.OrderBy(r => Comparable(r.GetValueOrDefault(name)), Comparer<IComparable?>.Default)
                    : ordered.ThenBy(r => Comparable(r.GetValueOrDefault(name)), Comparer<IComparable?>.Default);
            }

            rows = ordered ?? rows;
        }

        var size = PageSizePattern().Match(prefer) is { Success: true } sized ? int.Parse(sized.Groups[1].Value, CultureInfo.InvariantCulture) : 5000;
        var offset = query.TryGetValue("$skiptoken", out var token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        var all = rows.ToList();
        var page = all.Skip(offset).Take(size).ToList();
        var annotations = prefer.Contains("odata.include-annotations", StringComparison.Ordinal);
        var selected = query.TryGetValue("$select", out var select) ? select.Split(',').ToHashSet(StringComparer.Ordinal) : null;

        var value = new JsonArray();
        foreach (var row in page)
        {
            var item = new JsonObject { ["@odata.etag"] = "W/\"1\"" };
            foreach (var attribute in Attributes)
            {
                if (attribute.AttributeOf is not null || !attribute.IsValidForRead || (selected is not null && !selected.Contains(attribute.LogicalName)))
                {
                    continue;
                }

                item[attribute.Property] = Serialize(row.GetValueOrDefault(attribute.LogicalName), attribute);
                if (annotations && row.TryGetValue(attribute.LogicalName + "@label", out var label))
                {
                    item[attribute.Property + "@OData.Community.Display.V1.FormattedValue"] = label?.ToString();
                }

                if (annotations && row.TryGetValue(attribute.LogicalName + "@type", out var type))
                {
                    item[attribute.Property + "@Microsoft.Dynamics.CRM.lookuplogicalname"] = type?.ToString();
                }
            }

            value.Add(item);
        }

        var body = new JsonObject { ["@odata.context"] = $"{Environment}/api/data/v9.2/$metadata#{EntitySet}", ["value"] = value };
        if (offset + page.Count < all.Count)
        {
            var baseUrl = Regex.Replace(url.AbsoluteUri, @"&\$skiptoken=\d+", string.Empty);
            var next = $"{baseUrl}&$skiptoken={offset + page.Count}";
            body["@odata.nextLink"] = NextLinkRewrite is null ? next : NextLinkRewrite(next);
        }

        return Json(HttpStatusCode.OK, body.ToJsonString());
    }

    private static JsonNode? Serialize(object? value, FakeAttribute attribute) => value switch
    {
        null => null,
        Guid guid => guid.ToString("D"),
        DateTime date when attribute.DateTimeBehavior == "DateOnly" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        bool flag => flag,
        int whole => whole,
        long big => big,
        decimal number => number,
        double number => number,
        string text => text,
        JsonNode node => node.DeepClone(),
        _ => throw new InvalidOperationException($"The fake environment cannot write a {value.GetType().Name}."),
    };

    /// <summary>The conditions of a filter the reader writes: comparisons joined by <c>and</c>, each optionally in parentheses.</summary>
    private static List<Func<Dictionary<string, object?>, bool>> Conditions(string filter)
    {
        var conditions = new List<Func<Dictionary<string, object?>, bool>>();
        foreach (var part in filter.Split(" and ", StringSplitOptions.TrimEntries))
        {
            var text = part.Trim();
            while (text.StartsWith('(') && text.EndsWith(')'))
            {
                text = text[1..^1].Trim();
            }

            var match = ComparisonPattern().Match(text);
            if (!match.Success)
            {
                throw new InvalidOperationException($"The fake environment cannot evaluate the condition '{text}'.");
            }

            var column = match.Groups["column"].Value;
            var op = match.Groups["op"].Value;
            var literal = Literal(match.Groups["literal"].Value);
            conditions.Add(row =>
            {
                var cell = Comparable(row.GetValueOrDefault(column));
                if (cell is null)
                {
                    return false;
                }

                var compared = cell.CompareTo(literal);
                return op switch
                {
                    "eq" => compared == 0,
                    "ne" => compared != 0,
                    "gt" => compared > 0,
                    "ge" => compared >= 0,
                    "lt" => compared < 0,
                    "le" => compared <= 0,
                    _ => throw new InvalidOperationException($"Unknown operator {op}."),
                };
            });
        }

        return conditions;
    }

    private static IComparable Literal(string text)
    {
        if (text.StartsWith('\'') && text.EndsWith('\''))
        {
            return text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        if (DateOnlyPattern().IsMatch(text))
        {
            return DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (text.Contains('T', StringComparison.Ordinal) && text.EndsWith('Z'))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime;
        }

        return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static IComparable? Comparable(object? value) => value switch
    {
        null => null,
        int whole => (decimal)whole,
        long big => (decimal)big,
        double number => (decimal)number,
        decimal number => number,
        DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Unspecified),
        Guid guid => guid.ToString("D"),
        IComparable comparable => comparable,
        _ => value.ToString(),
    };

    private static Dictionary<string, string> Query(Uri url)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            query[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return query;
    }

    private static string? Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [GeneratedRegex(@"^EntityDefinitions\(LogicalName='(?<table>[^']*)'\)(?<rest>.*)$")]
    private static partial Regex DefinitionPattern();

    [GeneratedRegex(@"odata\.maxpagesize=(\d+)")]
    private static partial Regex PageSizePattern();

    [GeneratedRegex(@"^(?<column>\w+) (?<op>eq|ne|gt|ge|lt|le) (?<literal>.+)$")]
    private static partial Regex ComparisonPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateOnlyPattern();
}
