using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SqlFlow.Sources.Xml;

/// <summary>What an XPath points at: a value (leaf element or attribute), a container element, or a repeating element.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "XML's data model names these element/value; matching it reads clearer than a synonym.")]
public enum XmlNodeKind
{
    /// <summary>A scalar leaf (an element with only text, or an attribute) that becomes a column.</summary>
    Value,

    /// <summary>A container element; a valid target for rowXPath, xmlPaths, or excludePaths.</summary>
    Object,

    /// <summary>A repeating element (several same-named siblings); becomes a column, or a target for explode.</summary>
    Array,
}

/// <summary>A path discovered in a record together with what it points at.</summary>
public readonly record struct XmlPathNode(string Path, XmlNodeKind Kind);

/// <summary>One path in the aggregated inventory, with how many sampled records contained it.</summary>
public sealed record XmlPathInfo(string Path, XmlNodeKind Kind, int RecordCount);

/// <summary>Every addressable XPath found across a sample, including container and repeating paths.</summary>
public sealed record XmlPathInventory(int RecordsScanned, IReadOnlyList<XmlPathInfo> Paths)
{
    public int FilesScanned { get; init; }
}

/// <summary>
/// Builds a full inventory of the addressable XPaths in XML records (the read-only counterpart to the
/// flattener, used for the <c>paths</c>/<c>flatten</c>/<c>discover</c> commands). It surfaces attributes,
/// leaf elements, container elements, and repeating elements - including every element of a repeating set
/// (so heterogeneous fields are captured) and the element shape of repeats (so an exploded repeat surfaces
/// its column), mirroring the runtime flattener.
/// </summary>
public static class XmlPathInventoryBuilder
{
    /// <summary>Every addressable path in one record, with its kind, in document order.</summary>
    public static IReadOnlyList<XmlPathNode> ExtractTypedPaths(XElement record, int maxDepth)
    {
        var nodes = new List<XmlPathNode>();
        Walk(record, string.Empty, 0, maxDepth, nodes);
        return nodes;
    }

    /// <summary>Aggregates per-record path lists into the unified inventory (first-seen order, per-path counts).</summary>
    public static XmlPathInventory Build(IEnumerable<IReadOnlyList<XmlPathNode>> perRecordNodes)
    {
        ArgumentNullException.ThrowIfNull(perRecordNodes);

        var order = new List<string>();
        var kinds = new Dictionary<string, XmlNodeKind>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;

        foreach (var nodes in perRecordNodes)
        {
            scanned++;
            var countedThisRecord = new HashSet<string>(StringComparer.Ordinal);

            foreach (var node in nodes)
            {
                if (kinds.TryGetValue(node.Path, out var existing))
                {
                    if (Rank(node.Kind) > Rank(existing))
                    {
                        kinds[node.Path] = node.Kind;
                    }
                }
                else
                {
                    kinds[node.Path] = node.Kind;
                    counts[node.Path] = 0;
                    order.Add(node.Path);
                }

                if (countedThisRecord.Add(node.Path))
                {
                    counts[node.Path]++;
                }
            }
        }

        var paths = order.Select(path => new XmlPathInfo(path, kinds[path], counts[path])).ToList();
        return new XmlPathInventory(scanned, paths);
    }

    private static void Walk(XElement element, string path, int depth, int maxDepth, List<XmlPathNode> nodes)
    {
        if (depth > maxDepth)
        {
            return;
        }

        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration)
            {
                nodes.Add(new XmlPathNode($"{path}/@{attribute.Name.LocalName}", XmlNodeKind.Value));
            }
        }

        var childElements = element.Elements().ToList();

        // No child elements: a text leaf (it may still carry attributes). Emit its own text column, matching
        // the flattener, which falls back to the element name for the record root (empty path).
        if (childElements.Count == 0)
        {
            nodes.Add(new XmlPathNode(path.Length == 0 ? "/" + element.Name.LocalName : path, XmlNodeKind.Value));
            return;
        }

        // Mixed content: a container that also has its own direct text produces a text column too.
        if (HasDirectText(element))
        {
            nodes.Add(new XmlPathNode(path.Length == 0 ? "/" + element.Name.LocalName : path, XmlNodeKind.Value));
        }

        foreach (var group in childElements.GroupBy(e => e.Name.LocalName, StringComparer.Ordinal))
        {
            var childPath = $"{path}/{group.Key}";
            var elements = group.ToList();

            if (elements.Count == 1)
            {
                var child = elements[0];
                if (child.HasElements)
                {
                    nodes.Add(new XmlPathNode(childPath, XmlNodeKind.Object));
                }

                // Walk emits the child's attributes and, if it is a text leaf, its own Value node.
                Walk(child, childPath, depth + 1, maxDepth, nodes);
            }
            else
            {
                nodes.Add(new XmlPathNode(childPath, XmlNodeKind.Array));

                // Visit every element so heterogeneous fields - and a leaf sibling's own text - are surfaced.
                foreach (var element2 in elements)
                {
                    var elementPath = $"{childPath}[*]";
                    if (element2.HasElements)
                    {
                        nodes.Add(new XmlPathNode(elementPath, XmlNodeKind.Object));
                    }

                    Walk(element2, elementPath, depth + 1, maxDepth, nodes);
                }
            }
        }
    }

    private static bool HasDirectText(XElement element)
        => element.Nodes().OfType<XText>().Any(t => t.Value.Trim().Length > 0);

    // A text-leaf occurrence (Value) wins, so a path that carries text anywhere produces a column even when
    // it is also a container/repeat elsewhere (its child columns live under distinct paths).
    private static int Rank(XmlNodeKind kind) => kind switch
    {
        XmlNodeKind.Value => 3,
        XmlNodeKind.Array => 2,
        _ => 1,
    };

    /// <summary>A stable fingerprint of a record's path set (for schema-drift grouping).</summary>
    public static string Fingerprint(IReadOnlyList<XmlPathNode> nodes)
    {
        var sorted = nodes.Select(n => n.Path).Distinct(StringComparer.Ordinal).ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var path in sorted)
        {
            builder.Append(path).Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
