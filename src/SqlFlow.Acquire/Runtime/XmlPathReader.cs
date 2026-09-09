using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// The XML counterpart to <see cref="JsonPathReader"/>: reads records and values out of an XML response so an
/// XML-only endpoint gets the same discovery, fan-out, and empty-page handling a JSON one already has. SOAP is the
/// motivating case, where the ids to fan out over and the paging state both live in the response body.
/// </summary>
/// <remarks>
/// Namespace prefixes are stripped on parse, so paths are written against local names (<c>//Quest/QuestId</c>)
/// without declaring the envelope's namespaces in YAML. That mirrors the XML pre-ingestion reader, whose
/// <c>stripNamespacePrefixes</c> also defaults to on, and it is what keeps a path stable when a service moves its
/// payload to a new namespace revision. Paths are XPath, evaluated against the stripped document.
/// </remarks>
public static class XmlPathReader
{
    /// <summary>
    /// Parses a response body as XML, returning the namespace-stripped root, or null when the payload is not XML.
    /// The content type is only a hint: a served-as-text/plain SOAP body still parses, and an XML-labelled body that
    /// does not parse returns null rather than throwing, because a transport must not fail a fetch over a payload it
    /// was merely trying to inspect.
    /// </summary>
    public static XElement? TryParse(byte[] body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!LooksLikeXml(body, contentType))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,   // no external entity resolution on a remote payload
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });
            var document = XDocument.Load(reader);
            return document.Root is { } root ? StripNamespaces(root) : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>Whether a body is worth handing to the XML parser: an XML content type, or a leading angle bracket
    /// once any byte-order mark and leading whitespace are skipped.</summary>
    public static bool LooksLikeXml(byte[] body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (contentType is not null && contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var i = 0;
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            i = 3;
        }

        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < body.Length && body[i] == (byte)'<';
    }

    /// <summary>The elements an XPath selects, in document order.</summary>
    public static IReadOnlyList<XElement> SelectNodes(XElement root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            return root.XPathSelectElements(path).ToList();
        }
        catch (XPathException ex)
        {
            throw new SqlFlowException($"Invalid XML path '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>All values an XPath selects, projecting an element to its text and an attribute to its value.</summary>
    public static IReadOnlyList<string> SelectValues(XElement root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        object evaluated;
        try
        {
            evaluated = root.XPathEvaluate(path);
        }
        catch (XPathException ex)
        {
            throw new SqlFlowException($"Invalid XML path '{path}': {ex.Message}", ex);
        }

        if (evaluated is IEnumerable<object> nodes)
        {
            var values = new List<string>();
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case XElement element:
                        values.Add(element.Value);
                        break;
                    case XAttribute attribute:
                        values.Add(attribute.Value);
                        break;
                    case XText text:
                        values.Add(text.Value);
                        break;
                }
            }

            return values;
        }

        return evaluated is null ? [] : [Convert.ToString(evaluated, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty];
    }

    /// <summary>The first value an XPath selects, or null when it matches nothing.</summary>
    public static string? SelectValue(XElement root, string path)
    {
        var values = SelectValues(root, path);
        return values.Count > 0 ? values[0] : null;
    }

    /// <summary>
    /// Counts the records on a page for empty-page detection. An XML page has no array to auto-locate the way a JSON
    /// one does, so the count is only defined when <paramref name="recordsPath"/> names the record elements; without
    /// it the count is -1 ("unknown"), which leaves pagination to its page cap and the skip-empty policy off. A
    /// body-paged XML source therefore has to declare <c>recordsPath</c> to stop on its own last page.
    /// </summary>
    public static int CountRecords(XElement root, string? recordsPath)
    {
        ArgumentNullException.ThrowIfNull(root);
        return string.IsNullOrWhiteSpace(recordsPath) ? -1 : SelectNodes(root, recordsPath).Count;
    }

    /// <summary>The ordinally largest value of a path across the page, for a response-driven watermark.</summary>
    public static string? MaxValue(XElement root, string path)
    {
        string? max = null;
        foreach (var value in SelectValues(root, path))
        {
            if (max is null || string.CompareOrdinal(value, max) > 0)
            {
                max = value;
            }
        }

        return max;
    }

    /// <summary>
    /// Rebuilds an element tree with every name reduced to its local part and namespace declarations dropped, so
    /// XPath can address it without a prefix resolver. Attributes that collide once their namespace is removed keep
    /// the first occurrence in document order, since an <see cref="XElement"/> cannot carry two attributes of one
    /// name and the alternative is throwing on a payload that is otherwise perfectly readable.
    /// </summary>
    private static XElement StripNamespaces(XElement element)
    {
        var stripped = new XElement(element.Name.LocalName);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && seen.Add(attribute.Name.LocalName))
            {
                stripped.Add(new XAttribute(attribute.Name.LocalName, attribute.Value));
            }
        }

        foreach (var node in element.Nodes())
        {
            stripped.Add(node is XElement child ? StripNamespaces(child) : node);
        }

        return stripped;
    }

    /// <summary>Renders an element back to XML text, used when a selected record is landed or logged.</summary>
    public static string ToXml(XElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The UTF-8 bytes of an element's XML text.</summary>
    public static byte[] ToUtf8(XElement element) => Encoding.UTF8.GetBytes(ToXml(element));
}
