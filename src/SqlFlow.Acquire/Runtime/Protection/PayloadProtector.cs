using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>
/// Applies an item's <see cref="AcquireProtectRule"/> set to a raw payload before it is landed, so PII and
/// sensitive fields are scrubbed, pseudonymised, or generalised at the acquisition boundary and never persisted
/// raw. Format-aware: <c>json</c> payloads are protected through a mutable node tree, <c>jsonl</c> line by line,
/// <c>xml</c> through the element tree (a trailing <c>@name</c> segment addresses an attribute), and <c>csv</c> by
/// header column name (quote-aware; untouched fields keep their raw bytes). One instance per item per run: it holds
/// the resolved secret material, a run-scoped salt (for transaction scope), a cache of derived keys, and the
/// tokenisation map. A payload that is requested to be protected but cannot be parsed in its declared format
/// throws, so protection is never silently skipped; a format with no structural model (<c>bin</c>, <c>txt</c>,
/// spreadsheets) refuses protection loudly at the same point.
/// </summary>
public sealed class PayloadProtector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IReadOnlyList<AcquireProtectRule> _rules;
    private readonly IReadOnlyDictionary<string, string> _secrets;
    private readonly string _flowName;
    private readonly byte[] _runSalt;
    private readonly Dictionary<string, byte[]> _keyCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenCache = new(StringComparer.Ordinal);

    /// <param name="rules">The rules for this landing item; must be non-empty (callers skip protection when empty).</param>
    /// <param name="resolvedSecrets">Each rule's <c>${...}</c> secret reference mapped to its resolved plaintext.</param>
    /// <param name="flowName">The flow name, used as the relationship-scope salt so pseudonyms are consistent within the source.</param>
    public PayloadProtector(
        IReadOnlyList<AcquireProtectRule> rules,
        IReadOnlyDictionary<string, string> resolvedSecrets,
        string flowName)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(resolvedSecrets);
        if (rules.Count == 0)
        {
            throw new ArgumentException("a protector needs at least one rule.", nameof(rules));
        }

        _rules = rules;
        _secrets = resolvedSecrets;
        _flowName = flowName ?? string.Empty;
        _runSalt = RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>Apply every rule to the payload in its landed format and return the protected bytes. Throws when the
    /// payload cannot be parsed in that format (or the format has no structural model to protect through), so a
    /// protection-carrying flow never lands an unprotected payload.</summary>
    public byte[] Apply(ReadOnlyMemory<byte> content, string format)
    {
        if (content.Length == 0)
        {
            return [];
        }

        return format.ToLowerInvariant() switch
        {
            "json" => ApplyJson(content),
            "jsonl" => ApplyJsonLines(content),
            "xml" => XmlProtection.Apply(content, _rules, TransformScalar),
            "csv" => CsvProtection.Apply(content, _rules, TransformScalar),
            _ => throw new SqlFlowException(
                $"landing.protect supports json, jsonl, xml, and csv payloads; this item landed '{format}', which cannot be protected. Remove the rules or land a structured format."),
        };
    }

    /// <summary>The per-scalar transform shared by every format adapter: null result means "remove the field", any
    /// other result replaces the value. Numeric flag tells JSON adapters to keep the node numeric.</summary>
    internal (string? Result, bool Numeric) TransformScalar(string value, AcquireProtectRule rule)
        => rule.Action == AcquireProtectAction.Remove ? (null, false) : Transform(value, rule);

    private byte[] ApplyJson(ReadOnlyMemory<byte> content)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content.Span);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException(
                $"landing.protect is configured but the payload is not valid JSON, so it cannot be protected and will not be landed: {ex.Message}", ex);
        }

        if (root is null)
        {
            return content.ToArray();
        }

        foreach (var rule in _rules)
        {
            ApplyRule(root, rule);
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(SerializerOptions));
    }

    private byte[] ApplyJsonLines(ReadOnlyMemory<byte> content)
    {
        var text = Encoding.UTF8.GetString(content.Span);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailingNewline = text.EndsWith('\n');
        var lines = text.Split('\n');
        var protectedLines = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                protectedLines.Add(line);
                continue;
            }

            protectedLines.Add(Encoding.UTF8.GetString(ApplyJson(Encoding.UTF8.GetBytes(line))));
        }

        // Split on a trailing newline yields one empty tail entry; drop it so the join does not double it.
        if (trailingNewline && protectedLines.Count > 0 && protectedLines[^1].Length == 0)
        {
            protectedLines.RemoveAt(protectedLines.Count - 1);
        }

        var joined = string.Join(newline, protectedLines) + (trailingNewline ? newline : string.Empty);
        return Encoding.UTF8.GetBytes(joined);
    }

    private void ApplyRule(JsonNode root, AcquireProtectRule rule)
    {
        var segments = ProtectPath.Parse(rule.Path);
        if (segments.Count == 0)
        {
            throw new SqlFlowException($"landing.protect path '{rule.Path}' selects the whole document, which is not supported.");
        }

        var last = segments[^1];
        foreach (var parent in EvaluateParents(root, segments))
        {
            ApplyToParent(parent, last, rule);
        }
    }

    private void ApplyToParent(JsonNode parent, ProtectPath.Segment last, AcquireProtectRule rule)
    {
        var remove = rule.Action == AcquireProtectAction.Remove;
        switch (last.Kind)
        {
            case ProtectPath.Kind.Property when parent is JsonObject obj:
                if (remove)
                {
                    obj.Remove(last.Name!);
                }
                else if (obj.TryGetPropertyValue(last.Name!, out var node) && node is not null)
                {
                    obj[last.Name!] = TransformNode(node, rule);
                }

                break;
            case ProtectPath.Kind.Index when parent is JsonArray array && last.Index < array.Count:
                if (remove)
                {
                    array.RemoveAt(last.Index);
                }
                else if (array[last.Index] is { } indexed)
                {
                    array[last.Index] = TransformNode(indexed, rule);
                }

                break;
            case ProtectPath.Kind.Wildcard when parent is JsonArray array:
                if (remove)
                {
                    array.Clear();
                }
                else
                {
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is { } element)
                        {
                            array[i] = TransformNode(element, rule);
                        }
                    }
                }

                break;
            case ProtectPath.Kind.Wildcard when parent is JsonObject obj:
                if (remove)
                {
                    obj.Clear();
                }
                else
                {
                    foreach (var key in obj.Select(kv => kv.Key).ToList())
                    {
                        if (obj[key] is { } value)
                        {
                            obj[key] = TransformNode(value, rule);
                        }
                    }
                }

                break;
        }
    }

    private JsonNode TransformNode(JsonNode node, AcquireProtectRule rule)
    {
        var value = LeafString(node);
        var (result, numeric) = Transform(value, rule);
        if (numeric && decimal.TryParse(result, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(result);
    }

    private (string Result, bool Numeric) Transform(string value, AcquireProtectRule rule) => rule.Action switch
    {
        AcquireProtectAction.Redact => (PayloadTransforms.Redact(value, rule.Params), false),
        AcquireProtectAction.Mask => (PayloadTransforms.Mask(value, rule.Params), false),
        AcquireProtectAction.Hash => (PayloadTransforms.Hash(value), false),
        AcquireProtectAction.Hmac => (PayloadTransforms.Hmac(value, DeriveKey(rule, "hmac"), rule.Params), false),
        AcquireProtectAction.Tokenize => (Tokenize(value, rule), false),
        AcquireProtectAction.Encrypt => (PayloadTransforms.Encrypt(value, new ProtectionKeys(DeriveKey(rule, "enc"), DeriveKey(rule, "nonce"))), false),
        AcquireProtectAction.Generalize => (PayloadTransforms.Generalize(value, rule.Params), IsNumericGeneralize(rule)),
        AcquireProtectAction.Remove => throw new InvalidOperationException("remove is handled before value transforms."),
        _ => throw new SqlFlowException($"unknown protect action '{rule.Action}'."),
    };

    private static bool IsNumericGeneralize(AcquireProtectRule rule)
        => rule.Params.TryGetValue("mode", out var mode) && string.Equals(mode, "round", StringComparison.OrdinalIgnoreCase);

    private string Tokenize(string value, AcquireProtectRule rule)
    {
        var format = rule.Params.TryGetValue("format", out var f) && !string.IsNullOrEmpty(f) ? f : "tok_{short}";
        if (rule.Secret is not null)
        {
            // Deterministic keyed token: same value+key+scope -> same token, so downstream joins survive, but the
            // original is not recoverable from the token (use encrypt when it must be).
            var core = Convert.ToHexString(HMACSHA256.HashData(DeriveKey(rule, "token"), Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            return FormatToken(format, core);
        }

        // No key: a run-scoped random token per distinct value, cached so the payload stays internally consistent.
        var cacheKey = $"{rule.Path} {value}";
        if (!_tokenCache.TryGetValue(cacheKey, out var token))
        {
            token = FormatToken(format, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            _tokenCache[cacheKey] = token;
        }

        return token;
    }

    private static string FormatToken(string format, string hex)
    {
        var uuid = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
        if (format.Contains("{uuid}", StringComparison.Ordinal))
        {
            return format.Replace("{uuid}", uuid, StringComparison.Ordinal);
        }

        if (format.Contains("{short}", StringComparison.Ordinal))
        {
            return format.Replace("{short}", hex[..8], StringComparison.Ordinal);
        }

        return format is "" or "uuid" ? uuid : $"{format}_{hex[..8]}";
    }

    private byte[] DeriveKey(AcquireProtectRule rule, string purpose)
    {
        var secretRef = rule.Secret
            ?? throw new SqlFlowException($"protect action '{rule.Action}' on path '{rule.Path}' requires 'secret'.");
        if (!_secrets.TryGetValue(secretRef, out var plaintext))
        {
            throw new SqlFlowException($"protect secret '{secretRef}' was not resolved for path '{rule.Path}'.");
        }

        var relationship = rule.Params.TryGetValue("relationship", out var r) ? r : string.Empty;
        var cacheKey = $"{secretRef} {rule.Scope} {relationship} {purpose}";
        if (_keyCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var salt = rule.Scope switch
        {
            AcquireProtectScope.Person => [],
            AcquireProtectScope.Relationship => Encoding.UTF8.GetBytes($"{_flowName}|{relationship}"),
            AcquireProtectScope.Transaction => _runSalt,
            _ => Array.Empty<byte>(),
        };

        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(plaintext), 32, salt, Encoding.UTF8.GetBytes(purpose));
        _keyCache[cacheKey] = key;
        return key;
    }

    private static string LeafString(JsonNode node)
    {
        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        }

        return node.ToJsonString();
    }

    private static IReadOnlyList<JsonNode> EvaluateParents(JsonNode root, IReadOnlyList<ProtectPath.Segment> segments)
    {
        var current = new List<JsonNode> { root };
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = new List<JsonNode>();
            foreach (var node in current)
            {
                Descend(node, segments[i], next);
            }

            current = next;
        }

        return current;
    }

    private static void Descend(JsonNode node, ProtectPath.Segment segment, List<JsonNode> into)
    {
        switch (segment.Kind)
        {
            case ProtectPath.Kind.Property when node is JsonObject obj && obj.TryGetPropertyValue(segment.Name!, out var child) && child is not null:
                into.Add(child);
                break;
            case ProtectPath.Kind.Index when node is JsonArray array && segment.Index < array.Count && array[segment.Index] is { } indexed:
                into.Add(indexed);
                break;
            case ProtectPath.Kind.Wildcard when node is JsonArray array:
                foreach (var element in array)
                {
                    if (element is not null)
                    {
                        into.Add(element);
                    }
                }

                break;
            case ProtectPath.Kind.Wildcard when node is JsonObject obj:
                foreach (var kv in obj)
                {
                    if (kv.Value is not null)
                    {
                        into.Add(kv.Value);
                    }
                }

                break;
        }
    }

}
