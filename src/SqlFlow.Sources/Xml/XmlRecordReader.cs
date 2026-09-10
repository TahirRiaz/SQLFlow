using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using SqlFlow.Core;

namespace SqlFlow.Sources.Xml;

/// <summary>
/// Parses an XML file's bytes into its row (record) elements. The document is read with DTD processing and
/// external entity resolution disabled (XXE-safe), namespaces are stripped to local names by default (so
/// paths and columns are clean), and the rows are selected by the configured XPath - by default each direct
/// child of the root element, since data XML is typically a wrapper of repeating rows.
///
/// XElement trees are managed and keep their document alive by reference, so the returned records are safe
/// to hold and enumerate after this returns.
/// </summary>
public static class XmlRecordReader
{
    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static IReadOnlyList<XElement> ReadRecords(byte[] data, string rowXPath, bool stripNamespaces, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return [];
        }

        XDocument document;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(stream, SafeSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SqlFlowException($"Invalid XML in '{fileName}': {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null)
        {
            return [];
        }

        // Reject pathologically deep documents before any depth-recursive step runs on them. XDocument.Load is
        // iterative, but the downstream flatten and XElement.ToString (for kept / over-depth subtrees) recurse
        // per nesting level, so a few-kilobyte document nesting tens of thousands deep would otherwise overflow
        // the call stack and crash the process uncatchably. A generous bound turns that into a clear error.
        EnsureWithinDepth(root, fileName);

        var working = stripNamespaces ? new XDocument(StripNamespaces(root)) : document;
        return SelectRecords(working, rowXPath, fileName).ToList();
    }

    /// <summary>
    /// Parses one XML document and returns its root element WITHOUT selecting row elements: the raw tree the
    /// record-anchor detector walks before a row grain is chosen. Namespaces are stripped when
    /// <paramref name="stripNamespaces"/> is set, so the absolute paths the detector emits match the paths the
    /// row selector later resolves. Returns null for an empty document or one with no root element. The returned
    /// element keeps its document alive by reference, so it is safe to hold after this returns.
    /// </summary>
    public static XElement? ReadDocumentRoot(byte[] data, bool stripNamespaces, string fileName)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var reader = XmlReader.Create(stream, SafeSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SqlFlowException($"Invalid XML in '{fileName}': {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        EnsureWithinDepth(root, fileName);
        return stripNamespaces ? StripNamespaces(root) : root;
    }

    /// <summary>Generous bound on element nesting depth; deeper documents are rejected rather than risk a stack overflow.</summary>
    private const int MaxNestingDepth = 1000;

    private static void EnsureWithinDepth(XElement root, string fileName)
    {
        var stack = new Stack<(XElement Element, int Depth)>();
        stack.Push((root, 1));

        while (stack.Count > 0)
        {
            var (element, depth) = stack.Pop();
            if (depth > MaxNestingDepth)
            {
                throw new SqlFlowException(
                    $"XML in '{fileName}' nests elements deeper than {MaxNestingDepth} levels, which is rejected "
                    + "to avoid unbounded recursion. Pre-split the document or reduce its nesting.");
            }

            foreach (var child in element.Elements())
            {
                stack.Push((child, depth + 1));
            }
        }
    }

    private static IEnumerable<XElement> SelectRecords(XDocument document, string? rowXPath, string fileName)
    {
        var trimmed = (rowXPath ?? string.Empty).Trim();
        var root = document.Root!;

        // Default: the root wraps repeating rows, so each direct child element is a record.
        if (trimmed.Length == 0 || trimmed is "/*" or "*")
        {
            return root.Elements();
        }

        // The root element itself is a single record (an unwrapped document).
        if (trimmed is "." or "/")
        {
            return new[] { root };
        }

        try
        {
            return document.XPathSelectElements(trimmed).ToList();
        }
        catch (XPathException ex)
        {
            throw new SqlFlowException($"Invalid rowXPath '{rowXPath}' for '{fileName}': {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            // XPathSelectElements throws InvalidOperationException (not XPathException) when the expression is
            // syntactically valid but does not evaluate to an element node-set (e.g. count(...), an attribute,
            // or a string result). That is still a bad rowXPath, so surface the same clear message.
            throw new SqlFlowException(
                $"rowXPath '{rowXPath}' for '{fileName}' must select elements, not {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Recreates an element tree using local names only, dropping namespace declarations and prefixes. The walk
    /// is iterative with an explicit heap stack (not recursion) so a pathologically deep document - a few
    /// kilobytes can nest tens of thousands of elements - cannot overflow the call stack and crash the process
    /// with an uncatchable StackOverflowException. Document order, mixed-content text, and the keep-first rule
    /// for namespace-colliding attributes are preserved.
    /// </summary>
    private static XElement StripNamespaces(XElement root)
    {
        var rootClone = new XElement(root.Name.LocalName);
        var stack = new Stack<(XElement Source, XElement Clone)>();
        stack.Push((root, rootClone));

        while (stack.Count > 0)
        {
            var (source, clone) = stack.Pop();

            foreach (var attribute in source.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                var localName = attribute.Name.LocalName;
                // Two attributes can share a local name across namespaces; keep the first, drop later collisions.
                if (clone.Attribute(localName) is null)
                {
                    clone.Add(new XAttribute(localName, attribute.Value));
                }
            }

            // Child elements are appended now (empty) so order is preserved, then filled when popped; text nodes
            // are copied inline. The clone is built top-down, so depth lives on the heap stack, not the call stack.
            foreach (var node in source.Nodes())
            {
                switch (node)
                {
                    case XElement child:
                        var childClone = new XElement(child.Name.LocalName);
                        clone.Add(childClone);
                        stack.Push((child, childClone));
                        break;
                    case XText text:
                        clone.Add(new XText(text.Value));
                        break;
                }
            }
        }

        return rootClone;
    }
}
