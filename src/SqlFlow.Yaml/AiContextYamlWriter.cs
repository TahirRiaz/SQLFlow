using SqlFlow.Core.AiContext;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Yaml;

/// <summary>
/// Writes an <see cref="AiContextDocument"/> as the file <see cref="YamlAiContextLoader"/> reads, so content held
/// anywhere else (the semantic layer as it was edited in the GUI before contexts were files) can be carried into a
/// repository. Reading what this writes gives back the same document.
/// </summary>
public static class AiContextYamlWriter
{
    /// <summary>The YAML text of <paramref name="document"/>.</summary>
    public static string Write(AiContextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new YamlMappingNode { { YamlAiContextLoader.RootKey, Scalar(document.Name) } };
        AddText(root, "description", document.Description);
        AddText(root, "instructions", document.Instructions);

        var tables = new YamlSequenceNode();
        foreach (var table in document.Tables)
        {
            tables.Add(Table(table));
        }

        root.Add("tables", tables);

        if (document.Examples.Count > 0)
        {
            var examples = new YamlSequenceNode();
            foreach (var example in document.Examples)
            {
                var node = new YamlMappingNode { { "question", Scalar(example.Question) }, { "sql", Scalar(example.Sql) } };
                AddText(node, "datasource", example.Datasource);
                examples.Add(node);
            }

            root.Add("examples", examples);
        }

        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter { NewLine = "\n" };
        stream.Save(writer, assignAnchors: false);
        var text = writer.ToString();

        // YamlDotNet closes a document with an explicit end marker; a context file is one document and reads better without.
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    /// <summary>A table name as a context file writes it, bracketing a part that holds a dot or starts with a quote.</summary>
    public static string TableName(AiContextTableName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Database is null
            ? $"{Part(name.Schema)}.{Part(name.Name)}"
            : $"{Part(name.Database)}.{Part(name.Schema)}.{Part(name.Name)}";
    }

    private static string Part(string part)
        => part.Contains('.') || part.StartsWith('[') || part.StartsWith('"') || part != part.Trim()
            ? $"[{part.Replace("]", "]]", StringComparison.Ordinal)}]"
            : part;

    private static YamlMappingNode Table(AiContextTable table)
    {
        var node = new YamlMappingNode { { "table", Scalar(TableName(table.Table)) } };
        AddText(node, "datasource", table.Table.Datasource);
        AddText(node, "name", table.BusinessName);
        AddText(node, "description", table.Description);
        AddList(node, "synonyms", table.Synonyms);
        AddList(node, "key", table.Key);

        var described = table.Columns.Where(c => c.Description is not null || c.Synonyms.Count > 0).ToList();
        if (described.Count > 0)
        {
            var columns = new YamlSequenceNode();
            foreach (var column in described)
            {
                var entry = new YamlMappingNode { { "column", Scalar(column.Name) } };
                AddText(entry, "description", column.Description);
                AddList(entry, "synonyms", column.Synonyms);
                columns.Add(entry);
            }

            node.Add("columns", columns);
        }

        AddList(node, "deny", table.Deny);

        if (table.Measures.Count > 0)
        {
            var measures = new YamlSequenceNode();
            foreach (var measure in table.Measures)
            {
                var entry = new YamlMappingNode { { "name", Scalar(measure.Name) }, { "expression", Scalar(measure.Expression) } };
                AddText(entry, "description", measure.Description);
                measures.Add(entry);
            }

            node.Add("measures", measures);
        }

        if (table.Joins.Count > 0)
        {
            var joins = new YamlSequenceNode();
            foreach (var join in table.Joins)
            {
                var entry = new YamlMappingNode { { "to", Scalar(TableName(join.To)) } };
                AddText(entry, "datasource", join.To.Datasource);
                AddList(entry, "columns", join.Columns);
                if (!join.ToColumns.SequenceEqual(join.Columns, StringComparer.Ordinal))
                {
                    AddList(entry, "toColumns", join.ToColumns);
                }

                if (join.JoinType != AiContextJoinTypes.Inner)
                {
                    entry.Add("type", Scalar(join.JoinType));
                }

                AddText(entry, "description", join.Description);
                joins.Add(entry);
            }

            node.Add("joins", joins);
        }

        return node;
    }

    private static void AddText(YamlMappingNode node, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            node.Add(key, Scalar(value));
        }
    }

    private static void AddList(YamlMappingNode node, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var list = new YamlSequenceNode { Style = SequenceStyle.Flow };
        foreach (var value in values)
        {
            list.Add(Scalar(value));
        }

        node.Add(key, list);
    }

    /// <summary>A scalar in the style that keeps it readable: a block for multi-line text, quoted where plain text would
    /// read as something else, plain otherwise.</summary>
    private static YamlScalarNode Scalar(string value)
    {
        if (value.Contains('\n'))
        {
            return new YamlScalarNode(value.TrimEnd() + "\n") { Style = ScalarStyle.Literal };
        }

        return new YamlScalarNode(value) { Style = NeedsQuotes(value) ? ScalarStyle.DoubleQuoted : ScalarStyle.Any };
    }

    /// <summary>Whether plain text would be read back as something other than this string: a number, a boolean, null,
    /// or text YAML gives a meaning to (a leading indicator, a ': ' or ' #' inside, surrounding space).</summary>
    private static bool NeedsQuotes(string value)
        => value.Length == 0
            || value != value.Trim()
            || "-?:,[]{}#&*!|>'\"%@`".Contains(value[0])
            || value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value.EndsWith(':')
            || double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            || value.ToLowerInvariant() is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~";
}
