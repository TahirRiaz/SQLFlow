using System.Text.RegularExpressions;
using SqlFlow.Core.AiContext;
using SqlFlow.Core.Compute;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SqlFlow.Yaml;

/// <summary>What reading one AI context file gave: the context's name when the file states one readably, the context
/// when the file is valid, and every problem otherwise.</summary>
/// <param name="Name">The root <c>aiContext:</c> name, or null when the file does not state one that is valid.</param>
/// <param name="Document">The context, or null when there is any problem: a context is approved as a whole, so a file
/// with a problem proposes nothing until it is fixed.</param>
/// <param name="Problems">Each problem, prefixed with the file and the line it is on.</param>
public sealed record AiContextParse(string? Name, AiContextDocument? Document, IReadOnlyList<string> Problems);

/// <summary>
/// Reads an AI context file (<see cref="AiContextDocument"/>). The reading is strict: an unknown key is a problem
/// rather than something ignored, since a misspelt key would otherwise silently leave a description, a column or a
/// join out of what an admin approves. Every problem names the line it is on, so the file can be fixed from the
/// message alone. Resolving the tables against the catalog is the sync's work; this checks only what the file says.
/// </summary>
public static partial class YamlAiContextLoader
{
    /// <summary>The root key naming the context, which is also what marks a file as an AI context.</summary>
    public const string RootKey = "aiContext";

    /// <summary>Whether <paramref name="path"/> names an AI context file: <c>ai-context.yaml</c> or a name ending in
    /// <c>.ai-context.yaml</c>, in any folder of a repository.</summary>
    public static bool IsAiContextFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileName(path);
        return name.Equals("ai-context.yaml", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ai-context.yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="name"/> is a valid context name: lowercase letters, digits, <c>.</c>,
    /// <c>_</c> and <c>-</c>, starting with a letter or a digit, at most <see cref="AiContextLimits.MaxNameLength"/>
    /// characters.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    /// <summary>Whether <paramref name="name"/> is a valid measure name: a letter or underscore, then letters, digits
    /// or underscores, at most <see cref="AiContextLimits.MaxMeasureNameLength"/> characters.</summary>
    public static bool IsValidMeasureName(string name) => MeasureNamePattern().IsMatch(name);

    /// <summary>Reads <paramref name="yaml"/>; <paramref name="source"/> labels every problem (a repository path).</summary>
    public static AiContextParse Parse(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(source);
        var reader = new Reader(source);

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            using var text = new StringReader(yaml);
            stream.Load(text);
        }
        catch (YamlException ex)
        {
            reader.Problem((int)ex.Start.Line, $"the file is not valid YAML: {ex.Message}");
            return new AiContextParse(null, null, reader.Problems);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // YamlDotNet reports a duplicate mapping key and some scanner failures this way rather than as a YamlException.
            reader.Problem(0, $"the file is not valid YAML: {ex.Message}");
            return new AiContextParse(null, null, reader.Problems);
        }

        if (stream.Documents.Count != 1)
        {
            reader.Problem(0, stream.Documents.Count == 0
                ? "the file is empty; an AI context file starts with 'aiContext: <name>'."
                : $"the file holds {stream.Documents.Count} YAML documents; an AI context file holds one.");
            return new AiContextParse(null, null, reader.Problems);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            reader.Problem(Line(stream.Documents[0].RootNode), "the file must be a mapping that starts with 'aiContext: <name>'.");
            return new AiContextParse(null, null, reader.Problems);
        }

        var document = reader.Context(root);
        return reader.Problems.Count == 0 && document is not null
            ? new AiContextParse(document.Name, document, [])
            : new AiContextParse(reader.Name, null, reader.Problems);
    }

    private static int Line(YamlNode node) => (int)node.Start.Line;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex MeasureNamePattern();

    private sealed class Reader(string source)
    {
        private static readonly string[] RootKeys = [RootKey, "description", "instructions", "tables", "examples"];
        private static readonly string[] TableKeys =
            ["table", "datasource", "name", "description", "synonyms", "key", "columns", "deny", "measures", "joins"];
        private static readonly string[] ColumnKeys = ["column", "description", "synonyms"];
        private static readonly string[] MeasureKeys = ["name", "expression", "description"];
        private static readonly string[] JoinKeys = ["to", "datasource", "columns", "toColumns", "type", "description"];
        private static readonly string[] ExampleKeys = ["question", "sql", "datasource"];

        public List<string> Problems { get; } = [];

        public string? Name { get; private set; }

        public void Problem(int line, string message)
            => Problems.Add(line > 0 ? $"{source}:{line}: {message}" : $"{source}: {message}");

        public AiContextDocument? Context(YamlMappingNode root)
        {
            var map = Keys(root, RootKeys, "the file");

            var name = Text(map, RootKey, root, "the file", required: true, max: AiContextLimits.MaxNameLength);
            if (name is not null)
            {
                if (IsValidName(name))
                {
                    Name = name;
                }
                else
                {
                    Problem(Line(map[RootKey]),
                        $"'{name}' is not a valid context name: use lowercase letters, digits, '.', '_' and '-', starting "
                        + "with a letter or a digit.");
                }
            }

            var description = Text(map, "description", root, "the context", max: AiContextLimits.MaxDescriptionLength);
            var instructions = Text(map, "instructions", root, "the context", max: AiContextLimits.MaxInstructionsLength);

            var tables = new List<AiContextTable>();
            if (!map.TryGetValue("tables", out var tablesNode))
            {
                Problem(Line(root), "the context lists no 'tables'; a context opens at least one table.");
            }
            else
            {
                var items = Sequence(tablesNode, "'tables'");
                if (items.Count == 0 && tablesNode is YamlSequenceNode)
                {
                    Problem(Line(tablesNode), "'tables' is empty; a context opens at least one table.");
                }

                if (items.Count > AiContextLimits.MaxTables)
                {
                    Problem(Line(tablesNode), $"'tables' lists {items.Count} tables; a context holds at most {AiContextLimits.MaxTables}.");
                }

                var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (Table(item) is not { } table)
                    {
                        continue;
                    }

                    var identity = $"{table.Table.Display}|{table.Table.Datasource}";
                    if (seen.TryGetValue(identity, out var firstLine))
                    {
                        Problem(table.Line, $"table '{table.Table.Display}' is already described on line {firstLine}.");
                        continue;
                    }

                    seen[identity] = table.Line;
                    tables.Add(table);
                }
            }

            var measureNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in tables)
            {
                foreach (var measure in table.Measures)
                {
                    if (!measureNames.TryAdd(measure.Name, table.Table.Display))
                    {
                        Problem(table.Line,
                            $"measure '{measure.Name}' is also declared on table '{measureNames[measure.Name]}'; a measure name "
                            + "is unique across the estate.");
                    }
                }
            }

            var examples = new List<AiContextExample>();
            if (map.TryGetValue("examples", out var examplesNode))
            {
                var items = Sequence(examplesNode, "'examples'");
                if (items.Count > AiContextLimits.MaxExamples)
                {
                    Problem(Line(examplesNode), $"'examples' lists {items.Count} examples; a context holds at most {AiContextLimits.MaxExamples}.");
                }

                var questions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (Example(item) is not { } example)
                    {
                        continue;
                    }

                    if (questions.TryGetValue(example.Question, out var firstLine))
                    {
                        Problem(Line(item), $"the question '{example.Question}' is already given on line {firstLine}.");
                        continue;
                    }

                    questions[example.Question] = Line(item);
                    examples.Add(example);
                }
            }

            return name is null ? null : new AiContextDocument(name, description, instructions, tables, examples);
        }

        private AiContextTable? Table(YamlNode node)
        {
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), "each entry of 'tables' is a mapping that starts with 'table: <database.schema.name>'.");
                return null;
            }

            var line = Line(mapping);
            var map = Keys(mapping, TableKeys, "a table");
            var tableText = Text(map, "table", mapping, "a table", required: true, max: 3 * AiContextLimits.MaxIdentifierLength + 2);
            var datasource = Datasource(map, "datasource");
            var where = tableText is null ? "a table" : $"table '{tableText}'";
            var businessName = Text(map, "name", mapping, where, max: AiContextLimits.MaxBusinessNameLength);
            var description = Text(map, "description", mapping, where, max: AiContextLimits.MaxTableDescriptionLength);
            var synonyms = Synonyms(map, where);

            // Every column of the table is open but those it denies; 'columns' only says what columns mean.
            var deny = new List<string>();
            if (map.TryGetValue("deny", out var denyNode))
            {
                foreach (var column in Names(denyNode, $"'deny' of {where}").Where(c => Identifier(c, Line(denyNode), $"'deny' of {where}")))
                {
                    if (deny.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        Problem(Line(denyNode), $"column '{column}' is denied twice on {where}.");
                        continue;
                    }

                    deny.Add(column);
                }
            }

            var denied = deny.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var columns = new List<AiContextColumn>();
            if (map.TryGetValue("columns", out var columnsNode))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Sequence(columnsNode, $"'columns' of {where}"))
                {
                    if (Column(item, where) is not { } column)
                    {
                        continue;
                    }

                    if (!seen.Add(column.Name))
                    {
                        Problem(Line(item), $"column '{column.Name}' is described twice on {where}.");
                        continue;
                    }

                    if (denied.Contains(column.Name))
                    {
                        Problem(Line(item), $"column '{column.Name}' of {where} is both described and denied; a denied column is never shown to the assistant.");
                        continue;
                    }

                    columns.Add(column);
                }
            }

            var key = ColumnList(map, "key", where);
            foreach (var column in key.Where(denied.Contains))
            {
                Problem(Line(map["key"]), $"key column '{column}' of {where} is denied; a key is made of open columns.");
            }

            if (key.Distinct(StringComparer.OrdinalIgnoreCase).Count() != key.Count)
            {
                Problem(Line(map["key"]), $"the key of {where} names a column twice.");
            }

            var measures = new List<AiContextMeasure>();
            if (map.TryGetValue("measures", out var measuresNode))
            {
                foreach (var item in Sequence(measuresNode, $"'measures' of {where}"))
                {
                    if (Measure(item, where) is { } measure)
                    {
                        measures.Add(measure);
                    }
                }
            }

            var joins = new List<AiContextJoin>();
            if (map.TryGetValue("joins", out var joinsNode))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Sequence(joinsNode, $"'joins' of {where}"))
                {
                    if (Join(item, where, denied) is not { } join)
                    {
                        continue;
                    }

                    var identity = $"{join.To.Display}|{join.To.Datasource}|{string.Join(',', join.Columns)}|{string.Join(',', join.ToColumns)}";
                    if (!seen.Add(identity))
                    {
                        Problem(Line(item), $"the join from {where} to '{join.To.Display}' on these columns is declared twice.");
                        continue;
                    }

                    joins.Add(join);
                }
            }

            if (tableText is null || TableName(tableText, datasource, Line(map["table"])) is not { } table)
            {
                return null;
            }

            return new AiContextTable(table, businessName, description, synonyms, key, columns, deny, measures, joins, line);
        }

        private AiContextColumn? Column(YamlNode node, string where)
        {
            // Listing a name alone would read as opening the column, which it does not: every column is open unless denied.
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node),
                    $"each entry of 'columns' of {where} describes a column: 'column: <name>' with a description or synonyms. "
                    + "Every column is open unless it is under 'deny'.");
                return null;
            }

            var map = Keys(mapping, ColumnKeys, $"a column of {where}");
            var columnName = Text(map, "column", mapping, $"a column of {where}", required: true, max: AiContextLimits.MaxIdentifierLength);
            var columnWhere = columnName is null ? $"a column of {where}" : $"column '{columnName}' of {where}";
            var description = Text(map, "description", mapping, columnWhere, max: AiContextLimits.MaxColumnDescriptionLength);
            var synonyms = Synonyms(map, columnWhere);
            if (columnName is not null && !map.ContainsKey("description") && !map.ContainsKey("synonyms"))
            {
                Problem(Line(mapping), $"{columnWhere} gives neither a description nor synonyms, so there is nothing to say about it.");
                return null;
            }

            return columnName is not null && Identifier(columnName, Line(mapping), columnWhere)
                ? new AiContextColumn(columnName, description, synonyms)
                : null;
        }

        private AiContextMeasure? Measure(YamlNode node, string where)
        {
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"each measure of {where} is a mapping with 'name' and 'expression'.");
                return null;
            }

            var map = Keys(mapping, MeasureKeys, $"a measure of {where}");
            var name = Text(map, "name", mapping, $"a measure of {where}", required: true, max: AiContextLimits.MaxMeasureNameLength);
            var measureWhere = name is null ? $"a measure of {where}" : $"measure '{name}' of {where}";
            var expression = Text(map, "expression", mapping, measureWhere, required: true, max: AiContextLimits.MaxExpressionLength);
            var description = Text(map, "description", mapping, measureWhere, max: AiContextLimits.MaxMeasureOrJoinDescriptionLength);
            if (name is not null && !IsValidMeasureName(name))
            {
                Problem(Line(map["name"]),
                    $"'{name}' is not a valid measure name: a letter or underscore, then letters, digits or underscores.");
                return null;
            }

            return name is null || expression is null ? null : new AiContextMeasure(name, expression, description);
        }

        private AiContextJoin? Join(YamlNode node, string where, IReadOnlySet<string> denied)
        {
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), $"each join of {where} is a mapping with 'to' and 'columns'.");
                return null;
            }

            var map = Keys(mapping, JoinKeys, $"a join of {where}");
            var to = Text(map, "to", mapping, $"a join of {where}", required: true, max: 3 * AiContextLimits.MaxIdentifierLength + 2);
            var datasource = Datasource(map, "datasource");
            var joinWhere = to is null ? $"a join of {where}" : $"the join from {where} to '{to}'";
            var columns = ColumnList(map, "columns", joinWhere);
            if (!map.ContainsKey("columns"))
            {
                Problem(Line(mapping), $"{joinWhere} names no 'columns'.");
            }

            foreach (var column in columns.Where(denied.Contains))
            {
                Problem(Line(map["columns"]), $"{joinWhere} joins on '{column}', which the table denies.");
            }

            var toColumns = map.ContainsKey("toColumns") ? ColumnList(map, "toColumns", joinWhere) : columns;
            if (toColumns.Count != columns.Count)
            {
                Problem(Line(mapping), $"{joinWhere} pairs {columns.Count} column(s) with {toColumns.Count}; they pair by position.");
            }

            var type = AiContextJoinTypes.Inner;
            if (Text(map, "type", mapping, joinWhere, max: 16) is { } given)
            {
                type = given.ToLowerInvariant();
                if (type is not (AiContextJoinTypes.Inner or AiContextJoinTypes.Left))
                {
                    Problem(Line(map["type"]), $"{joinWhere} has type '{given}'; a join is 'inner' or 'left'.");
                    return null;
                }
            }

            var description = Text(map, "description", mapping, joinWhere, max: AiContextLimits.MaxMeasureOrJoinDescriptionLength);
            if (to is null || columns.Count == 0 || toColumns.Count != columns.Count
                || TableName(to, datasource, Line(map["to"])) is not { } target)
            {
                return null;
            }

            return new AiContextJoin(target, columns, toColumns, type, description);
        }

        private AiContextExample? Example(YamlNode node)
        {
            if (node is not YamlMappingNode mapping)
            {
                Problem(Line(node), "each example is a mapping with 'question' and 'sql'.");
                return null;
            }

            var map = Keys(mapping, ExampleKeys, "an example");
            var question = Text(map, "question", mapping, "an example", required: true, max: AiContextLimits.MaxQuestionLength);
            var where = question is null ? "an example" : $"the example '{question}'";
            var sql = Text(map, "sql", mapping, where, required: true, max: AiContextLimits.MaxExampleSqlLength);
            var datasource = Datasource(map, "datasource");
            return question is null || sql is null ? null : new AiContextExample(question, sql, datasource);
        }

        /// <summary>The mapping's entries by key, each unknown key reported once.</summary>
        private Dictionary<string, YamlNode> Keys(YamlMappingNode mapping, string[] known, string where)
        {
            var map = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
            foreach (var (keyNode, value) in mapping.Children)
            {
                if (keyNode is not YamlScalarNode { Value: { } key })
                {
                    Problem(Line(keyNode), $"{where} has a key that is not text.");
                    continue;
                }

                if (!known.Contains(key, StringComparer.Ordinal))
                {
                    var near = known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    Problem(Line(keyNode), near is not null
                        ? $"{where} has key '{key}'; keys are case-sensitive, so write '{near}'."
                        : $"{where} has an unknown key '{key}'; it may have: {string.Join(", ", known)}.");
                    continue;
                }

                map[key] = value;
            }

            return map;
        }

        private string? Text(
            Dictionary<string, YamlNode> map, string key, YamlNode owner, string where, bool required = false, int max = int.MaxValue)
        {
            if (!map.TryGetValue(key, out var node))
            {
                if (required)
                {
                    Problem(Line(owner), $"{where} has no '{key}'.");
                }

                return null;
            }

            if (node is not YamlScalarNode scalar)
            {
                Problem(Line(node), $"'{key}' of {where} must be text.");
                return null;
            }

            var value = scalar.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (required)
                {
                    Problem(Line(node), $"'{key}' of {where} is blank.");
                }

                return null;
            }

            if (value.Length > max)
            {
                Problem(Line(node), $"'{key}' of {where} is {value.Length} characters; it may be at most {max}.");
                return null;
            }

            return value;
        }

        private string? Datasource(Dictionary<string, YamlNode> map, string key)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } value)
            {
                Problem(Line(node), $"'{key}' must be a connection reference such as ${{env:NAME}} or @alias.");
                return null;
            }

            if (value.Length > AiContextLimits.MaxDatasourceLength || !ComputeTaskPayload.IsWholeReference(value))
            {
                // Never echo the value: a literal here is most likely a connection string carrying a secret.
                Problem(Line(node),
                    $"'{key}' must be one connection reference (${{env:NAME}}, ${{keyvault:NAME}} or @alias), never a literal "
                    + "connection string.");
                return null;
            }

            return value;
        }

        private List<YamlNode> Sequence(YamlNode node, string what)
        {
            if (node is YamlSequenceNode sequence)
            {
                return [.. sequence.Children];
            }

            Problem(Line(node), $"{what} must be a list.");
            return [];
        }

        /// <summary>A list of names, written as a YAML list or, for one name, as plain text.</summary>
        private List<string> Names(YamlNode node, string what)
        {
            var nodes = node is YamlScalarNode ? [node] : Sequence(node, what);
            var names = new List<string>(nodes.Count);
            foreach (var item in nodes)
            {
                if (item is not YamlScalarNode { Value: { } raw } || raw.Trim() is not { Length: > 0 } value)
                {
                    Problem(Line(item), $"each entry of {what} must be non-blank text.");
                    continue;
                }

                names.Add(value);
            }

            return names;
        }

        private List<string> ColumnList(Dictionary<string, YamlNode> map, string key, string where)
        {
            if (!map.TryGetValue(key, out var node))
            {
                return [];
            }

            var columns = Names(node, $"'{key}' of {where}");
            var valid = new List<string>(columns.Count);
            foreach (var column in columns.Where(c => Identifier(c, Line(node), $"'{key}' of {where}")))
            {
                // A key and a join's columns are stored comma-joined, so a column whose name holds a comma cannot be one.
                if (column.Contains(','))
                {
                    Problem(Line(node), $"'{key}' of {where} names '{column}', whose comma keeps it from being a key or join column.");
                    continue;
                }

                valid.Add(column);
            }

            if (string.Join('\n', valid).Length > AiContextLimits.MaxColumnListStoredLength)
            {
                Problem(Line(node), $"'{key}' of {where} is longer than {AiContextLimits.MaxColumnListStoredLength} characters in all.");
                return [];
            }

            return valid;
        }

        private List<string> Synonyms(Dictionary<string, YamlNode> map, string where)
        {
            if (!map.TryGetValue("synonyms", out var node))
            {
                return [];
            }

            var synonyms = Names(node, $"'synonyms' of {where}");
            var line = Line(node);
            if (synonyms.Count > AiContextLimits.MaxSynonyms)
            {
                Problem(line, $"{where} has {synonyms.Count} synonyms; at most {AiContextLimits.MaxSynonyms} keep a search precise.");
                return [];
            }

            var distinct = new List<string>(synonyms.Count);
            foreach (var synonym in synonyms)
            {
                if (synonym.Length > AiContextLimits.MaxSynonymLength || synonym.Contains('\n') || synonym.Contains('\r'))
                {
                    Problem(line, $"synonym '{Clip(synonym)}' of {where} must be one line of at most {AiContextLimits.MaxSynonymLength} characters.");
                    continue;
                }

                if (distinct.Contains(synonym, StringComparer.OrdinalIgnoreCase))
                {
                    Problem(line, $"synonym '{synonym}' of {where} is given twice.");
                    continue;
                }

                distinct.Add(synonym);
            }

            if (string.Join('\n', distinct).Length > AiContextLimits.MaxSynonymsStoredLength)
            {
                Problem(line, $"the synonyms of {where} are longer than {AiContextLimits.MaxSynonymsStoredLength} characters in all.");
                return [];
            }

            return distinct;
        }

        private bool Identifier(string? name, int line, string where)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                Problem(line, $"{where} is blank.");
                return false;
            }

            if (name.Length > AiContextLimits.MaxIdentifierLength || name.Contains('\n') || name.Contains('\r'))
            {
                Problem(line, $"'{Clip(name)}' ({where}) is not a column name: one line of at most {AiContextLimits.MaxIdentifierLength} characters.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads <c>database.schema.name</c> or <c>schema.name</c>. A part may be bracketed (<c>[Order Lines]</c>) or
        /// double-quoted when it holds a dot or a space; <c>]]</c> and <c>""</c> stand for the closing character.
        /// </summary>
        private AiContextTableName? TableName(string text, string? datasource, int line)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            var i = 0;
            var valid = true;
            while (i < text.Length && valid)
            {
                var c = text[i];
                if (c is '[' or '"' && current.Length == 0)
                {
                    var close = c == '[' ? ']' : '"';
                    i++;
                    var closed = false;
                    while (i < text.Length)
                    {
                        if (text[i] == close)
                        {
                            if (i + 1 < text.Length && text[i + 1] == close)
                            {
                                current.Append(close);
                                i += 2;
                                continue;
                            }

                            closed = true;
                            i++;
                            break;
                        }

                        current.Append(text[i]);
                        i++;
                    }

                    valid = closed && (i == text.Length || text[i] == '.');
                    continue;
                }

                if (c == '.')
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    i++;
                    continue;
                }

                current.Append(c);
                i++;
            }

            parts.Add(current.ToString());
            if (!valid || parts.Count is < 2 or > 3 || parts.Any(p => p.Trim().Length == 0 || p.Length > AiContextLimits.MaxIdentifierLength))
            {
                Problem(line,
                    $"'{Clip(text)}' is not a table name: write database.schema.name (or schema.name), bracketing a part that "
                    + "holds a dot, as in [Sales Mart].dbo.[Order Lines].");
                return null;
            }

            return parts.Count == 3
                ? new AiContextTableName(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), datasource)
                : new AiContextTableName(null, parts[0].Trim(), parts[1].Trim(), datasource);
        }

        private static string Clip(string value) => value.Length <= 60 ? value : value[..60] + "...";
    }
}
