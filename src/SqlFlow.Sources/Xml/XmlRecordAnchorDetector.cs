using System.Xml.Linq;

namespace SqlFlow.Sources.Xml;

/// <summary>
/// Statistics-driven detection of the row-grain element in XML documents. XML always wraps records in at
/// least a root element, so the analog of the JSON envelope is universal:
/// <c>&lt;rss&gt;&lt;channel&gt;&lt;item/&gt;..&lt;/channel&gt;&lt;/rss&gt;</c>,
/// <c>&lt;catalog&gt;&lt;book/&gt;..&lt;/catalog&gt;</c>, <c>&lt;orders&gt;&lt;order/&gt;..&lt;/orders&gt;</c>.
/// The runtime default row grain (each direct child of the root) mis-grains anything nested one level deeper:
/// an RSS feed's rows sit under <c>channel</c>, a SOAP body's under the envelope, so the default selects the
/// single wrapper instead of the records.
///
/// This detector walks the document trees and returns the outermost element seen repeating inside a single
/// parent instance in any sample (its occurrence count exceeded its parent's), as an absolute XPath the runtime
/// row selector accepts verbatim (for example <c>/rss/channel/item</c>). It returns null when nothing repeats,
/// so a flat single-record document keeps the engine default. It mirrors the delta-forge
/// <c>detect_xml_record_anchor</c> translator: repetition is observed per document and OR-ed across the sample
/// (a corpus where every file holds exactly one element repeats nothing and declines), an explicit rowXPath
/// always wins over detection, and the detected value is correctness-equivalent to a hand-specified grain.
/// </summary>
public sealed class XmlRecordAnchorDetector
{
    /// <summary>Aggregated repetition statistics for one absolute element path, unioned across every sample.</summary>
    private sealed class NodeStats
    {
        /// <summary>True once this element was seen repeating inside a single parent instance in any sample. OR-ed
        /// across samples rather than summed: summing occurrence and parent counts would cancel out for a corpus
        /// where every file holds exactly one element, hiding a list that simply had one entry per file.</summary>
        public bool Repeats;

        /// <summary>Times this element was encountered, summed across samples. Ranking only.</summary>
        public long TotalOccurrences;

        /// <summary>Depth from the document (root element = 1). The outermost repeater wins.</summary>
        public int Depth;

        /// <summary>Distinct child field count (elements plus attributes) of the richest instance. A tie-break.</summary>
        public int ChildFields;
    }

    private readonly SortedDictionary<string, NodeStats> _stats = new(StringComparer.Ordinal);

    /// <summary>Per-document scratch: occurrence count, depth, parent path, and field count for one path.</summary>
    private sealed class DocNode
    {
        public int Count;
        public int Depth;
        public int ChildFields;
        public string ParentPath = string.Empty;
    }

    /// <summary>
    /// Accumulates one document's per-path repetition into the running aggregate. <paramref name="root"/> is the
    /// document's root element (namespace-stripped when the load strips namespaces, so the detected path matches
    /// the runtime row selector). The walk is iterative so a deeply nested document cannot overflow the stack.
    /// </summary>
    public void Accumulate(XElement root)
    {
        ArgumentNullException.ThrowIfNull(root);

        // Count element instances per absolute path within THIS document, with each path's depth, parent, and
        // richest field count. Repetition is then judged per document (child count exceeds parent count).
        var doc = new Dictionary<string, DocNode>(StringComparer.Ordinal);
        var stack = new Stack<(XElement Element, string Path, string Parent, int Depth)>();
        stack.Push((root, "/" + root.Name.LocalName, string.Empty, 1));

        while (stack.Count > 0)
        {
            var (element, path, parent, depth) = stack.Pop();
            if (!doc.TryGetValue(path, out var node))
            {
                node = new DocNode { Depth = depth, ParentPath = parent };
                doc[path] = node;
            }

            node.Count++;

            var childNames = element.Elements().Select(e => e.Name.LocalName).Distinct(StringComparer.Ordinal).Count();
            var attributeCount = element.Attributes().Count(a => !a.IsNamespaceDeclaration);
            var fields = childNames + attributeCount;
            if (fields > node.ChildFields)
            {
                node.ChildFields = fields;
            }

            foreach (var child in element.Elements())
            {
                stack.Push((child, path + "/" + child.Name.LocalName, path, depth + 1));
            }
        }

        foreach (var (path, node) in doc)
        {
            // The document is the conceptual parent of the root element; it occurs exactly once.
            var parentCount = node.ParentPath.Length == 0
                ? 1
                : doc.TryGetValue(node.ParentPath, out var parentNode) ? parentNode.Count : 1;
            var repeatsInThisDocument = node.Count > parentCount;

            if (!_stats.TryGetValue(path, out var stats))
            {
                stats = new NodeStats();
                _stats[path] = stats;
            }

            stats.TotalOccurrences += node.Count;
            stats.Depth = node.Depth;
            if (node.ChildFields > stats.ChildFields)
            {
                stats.ChildFields = node.ChildFields;
            }

            if (repeatsInThisDocument)
            {
                stats.Repeats = true;
            }
        }
    }

    /// <summary>
    /// Returns the absolute XPath whose elements should become table rows (for example <c>/rss/channel/item</c>),
    /// or null to keep the engine default row grain. Call after every sample has been accumulated.
    /// </summary>
    public string? Detect()
    {
        var candidates = new List<Candidate>();
        foreach (var (path, stats) in _stats)
        {
            if (stats.Repeats)
            {
                candidates.Add(new Candidate(path, stats.Depth, stats.TotalOccurrences, stats.ChildFields));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Outermost repeater first (so book wins over its repeating author children), then most rows, then
        // richest record, then path order for a deterministic tie-break.
        candidates.Sort(static (a, b) =>
        {
            var c = a.Depth.CompareTo(b.Depth);
            if (c != 0) return c;
            c = b.Occurrences.CompareTo(a.Occurrences);
            if (c != 0) return c;
            c = b.ChildFields.CompareTo(a.ChildFields);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Path, b.Path);
        });

        return candidates[0].Path;
    }

    /// <summary>Convenience for one-shot detection over an in-memory set of document roots.</summary>
    public static string? Detect(IEnumerable<XElement> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var detector = new XmlRecordAnchorDetector();
        foreach (var root in roots)
        {
            detector.Accumulate(root);
        }

        return detector.Detect();
    }

    /// <summary>One viable XML record element, flattened to its ranking inputs.</summary>
    private readonly record struct Candidate(string Path, int Depth, long Occurrences, int ChildFields);
}
