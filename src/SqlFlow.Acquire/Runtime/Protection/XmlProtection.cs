using System.Text;
using System.Xml;
using System.Xml.Linq;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>
/// The XML adapter for landing-time protection. The rule path walks the element tree starting AT the document root
/// (so <c>catalog.book.author</c> protects every <c>&lt;author&gt;</c> under every <c>&lt;book&gt;</c>; a repeated
/// element name matches all its occurrences, no explicit <c>[*]</c> needed). A trailing <c>@name</c> segment
/// addresses an attribute of the selected element(s). <c>remove</c> deletes the element/attribute; every other
/// action replaces its text value. The document is re-serialised without reformatting (whitespace preserved), so
/// untouched content keeps its shape.
/// </summary>
internal static class XmlProtection
{
    public static byte[] Apply(
        ReadOnlyMemory<byte> content,
        IReadOnlyList<AcquireProtectRule> rules,
        Func<string, AcquireProtectRule, (string? Result, bool Numeric)> transform)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(Encoding.UTF8.GetString(content.Span), LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new SqlFlowException(
                $"landing.protect is configured but the payload is not valid XML, so it cannot be protected and will not be landed: {ex.Message}", ex);
        }

        if (document.Root is not { } root)
        {
            return content.ToArray();
        }

        foreach (var rule in rules)
        {
            ApplyRule(root, rule, transform);
        }

        // Keep the declaration when the payload carried one; SaveOptions.DisableFormatting preserves whitespace.
        //
        // The writer MUST report UTF-8. XDocument.Save stamps the writer's own encoding into the declaration, and a
        // plain StringWriter reports UTF-16, so the saved text would announce encoding="utf-16" while the bytes
        // below are UTF-8 - a document every strict parser rejects with "no Unicode byte order mark".
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
        {
            document.Save(writer, SaveOptions.DisableFormatting);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void ApplyRule(
        XElement root,
        AcquireProtectRule rule,
        Func<string, AcquireProtectRule, (string? Result, bool Numeric)> transform)
    {
        var segments = ProtectPath.Parse(rule.Path);
        if (segments.Count == 0)
        {
            throw new SqlFlowException($"landing.protect path '{rule.Path}' selects the whole document, which is not supported.");
        }

        var last = segments[^1];
        var attributeName = last is { Kind: ProtectPath.Kind.Property, Name: { } n } && n.StartsWith('@') ? n[1..] : null;

        // Walk to the parents of the addressed node: the first segment matches the document root itself.
        var current = Match(root, segments[0]) ? new List<XElement> { root } : [];
        for (var i = 1; i < segments.Count - 1; i++)
        {
            current = Children(current, segments[i]);
        }

        if (attributeName is not null)
        {
            foreach (var element in current)
            {
                if (element.Attribute(attributeName) is { } attribute)
                {
                    var (result, _) = transform(attribute.Value, rule);
                    if (result is null)
                    {
                        attribute.Remove();
                    }
                    else
                    {
                        attribute.Value = result;
                    }
                }
            }

            return;
        }

        // Element address: when the path has one segment the target is the root itself, otherwise the last-step children.
        var targets = segments.Count == 1 ? current : Children(current, last);
        foreach (var element in targets.ToList())
        {
            var (result, _) = transform(element.Value, rule);
            if (result is null)
            {
                element.Remove();
            }
            else
            {
                element.Value = result;
            }
        }
    }

    private static List<XElement> Children(List<XElement> parents, ProtectPath.Segment segment)
    {
        var next = new List<XElement>();
        foreach (var parent in parents)
        {
            switch (segment.Kind)
            {
                case ProtectPath.Kind.Property:
                    next.AddRange(parent.Elements().Where(e => e.Name.LocalName == segment.Name));
                    break;
                case ProtectPath.Kind.Wildcard:
                    next.AddRange(parent.Elements());
                    break;
                case ProtectPath.Kind.Index:
                    var children = parent.Elements().ToList();
                    if (segment.Index < children.Count)
                    {
                        next.Add(children[segment.Index]);
                    }

                    break;
            }
        }

        return next;
    }

    private static bool Match(XElement element, ProtectPath.Segment segment) => segment.Kind switch
    {
        ProtectPath.Kind.Property => element.Name.LocalName == segment.Name,
        ProtectPath.Kind.Wildcard => true,
        _ => false,
    };

    /// <summary>A <see cref="StringWriter"/> that reports UTF-8, so the declaration XDocument.Save writes matches
    /// the UTF-8 bytes the protected payload is actually landed as.</summary>
    private sealed class Utf8StringWriter(StringBuilder builder)
        : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
