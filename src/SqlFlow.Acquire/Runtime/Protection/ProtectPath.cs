using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>
/// The parsed form of a protect rule's <c>path</c>, shared by the JSON and XML adapters: an optional leading
/// <c>$</c>, dotted names, <c>[*]</c> wildcards, and explicit indices. The XML adapter additionally reads a trailing
/// <c>@name</c> property as an attribute address; the CSV adapter does not parse at all (its path is a column name).
/// </summary>
internal static class ProtectPath
{
    public static IReadOnlyList<Segment> Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = new List<Segment>();
        var i = path.Length > 0 && path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = path.IndexOf(']', i + 1);
                if (close < 0)
                {
                    throw new SqlFlowException($"unterminated '[' in protect path '{path}'.");
                }

                var inner = path[(i + 1)..close].Trim().Trim('\'', '"');
                segments.Add(inner == "*"
                    ? Segment.Wildcard()
                    : int.TryParse(inner, out var index)
                        ? Segment.OfIndex(index)
                        : Segment.OfProperty(inner));
                i = close + 1;
                continue;
            }

            var start = i;
            while (i < path.Length && path[i] != '.' && path[i] != '[')
            {
                i++;
            }

            var name = path[start..i];
            segments.Add(name == "*" ? Segment.Wildcard() : Segment.OfProperty(name));
        }

        return segments;
    }

    public enum Kind
    {
        Property,
        Index,
        Wildcard,
    }

    public readonly record struct Segment(Kind Kind, string? Name, int Index)
    {
        public static Segment OfProperty(string name) => new(Kind.Property, name, 0);

        public static Segment OfIndex(int index) => new(Kind.Index, null, index);

        public static Segment Wildcard() => new(Kind.Wildcard, null, 0);
    }
}
