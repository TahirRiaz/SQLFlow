namespace SqlFlow.Sources.Xml;

/// <summary>One output column of an XML flatten formula: its name, source path, and whether it holds large text.</summary>
public sealed record XmlFlattenColumn(string Name, string SourcePath, bool IsLargeText);

/// <summary>The resolved output columns of an XML flatten and the collision mappings that keep it lossless.</summary>
public sealed record XmlFlattenFormula(
    IReadOnlyList<XmlFlattenColumn> Columns,
    IReadOnlyDictionary<string, string> CollisionMappings);

/// <summary>
/// Derives an <see cref="XmlFlattenFormula"/> by unioning the resolved schema columns the
/// <see cref="XmlPathFlattener"/> produces across the sampled records, preserving first-seen order. Because
/// the formula is the flattener's own output (not a re-implementation of its projection rules), it cannot
/// drift from the load: every column the flatten emits - and only those - appears, with the same name the
/// load uses. Collisions are resolved inside the flattener (lossless, deterministic), so no separate
/// collision-mapping pass is needed.
/// </summary>
public static class XmlFlattenFormulaBuilder
{
    private static readonly IReadOnlyDictionary<string, string> NoCollisionMappings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static XmlFlattenFormula FromRecords(IEnumerable<IReadOnlyList<XmlFlattenColumn>> perRecordColumns)
    {
        ArgumentNullException.ThrowIfNull(perRecordColumns);

        var columns = new List<XmlFlattenColumn>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in perRecordColumns)
        {
            foreach (var column in record)
            {
                if (seen.Add(column.Name))
                {
                    columns.Add(column);
                }
            }
        }

        return new XmlFlattenFormula(columns, NoCollisionMappings);
    }
}
