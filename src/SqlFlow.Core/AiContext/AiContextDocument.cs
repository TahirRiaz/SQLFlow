namespace SqlFlow.Core.AiContext;

/// <summary>
/// One AI context file as written: an <c>ai-context.yaml</c> (or <c>*.ai-context.yaml</c>) in a synced repository,
/// naming the context with its root <c>aiContext:</c> key. A context is what an AI assistant is given when it answers a
/// question from warehouse data: the tables and columns it may use, what they mean, how they join, the measures to
/// reuse, example questions with the SQL that answers them, and instructions for every query it writes. A file only
/// proposes a context: nothing in it reaches an assistant until an admin approves that exact version.
/// </summary>
/// <param name="Name">The context's name, unique across the estate. It is what an admin approves, so moving or
/// renaming the file keeps the approval, while renaming the context makes it a new one.</param>
/// <param name="Description">What the context covers, in a sentence, for people and for the assistant choosing
/// between contexts.</param>
/// <param name="Instructions">Guidance the assistant follows for every query over this context's tables.</param>
/// <param name="Tables">The tables (and views) the context opens, each with every column but those it denies.</param>
/// <param name="Examples">Questions this context answers, each with the SQL that answers it.</param>
public sealed record AiContextDocument(
    string Name,
    string? Description,
    string? Instructions,
    IReadOnlyList<AiContextTable> Tables,
    IReadOnlyList<AiContextExample> Examples);

/// <summary>A table or view as a context file names it: <c>database.schema.name</c>, or <c>schema.name</c> where the
/// catalog holds the name in one database only, with the datasource it lives on when the name exists on more than
/// one.</summary>
/// <param name="Database">The database, or null when the file gave only <c>schema.name</c>.</param>
/// <param name="Schema">The schema.</param>
/// <param name="Name">The object's name.</param>
/// <param name="Datasource">The connection reference (<c>${env:...}</c>, <c>${keyvault:...}</c> or <c>@alias</c>) the
/// estate's flows declare for it, or null.</param>
public sealed record AiContextTableName(string? Database, string Schema, string Name, string? Datasource)
{
    /// <summary>The name as a person reads it: <c>database.schema.name</c> or <c>schema.name</c>.</summary>
    public string Display => Database is null ? $"{Schema}.{Name}" : $"{Database}.{Schema}.{Name}";
}

/// <summary>One table of a context: its business context, the columns it describes and denies, and its measures and
/// joins. A table in a context opens every one of its columns except those it denies.</summary>
/// <param name="Table">Which catalogued object this is.</param>
/// <param name="BusinessName">The name a business reader knows the table by.</param>
/// <param name="Description">What one row represents and what a query author must know about it.</param>
/// <param name="Synonyms">Other words people use for it.</param>
/// <param name="Key">The columns identifying one row, in order; none of them denied.</param>
/// <param name="Columns">The columns the file describes, in the order written. Describing a column does not open it:
/// every column not denied is open.</param>
/// <param name="Deny">The columns the assistant may not use: closed to its schema and to every query it runs.</param>
/// <param name="Measures">Named SQL expressions over this table's open columns.</param>
/// <param name="Joins">How this table joins to others.</param>
/// <param name="Line">The line the entry starts on in its file, for messages.</param>
public sealed record AiContextTable(
    AiContextTableName Table,
    string? BusinessName,
    string? Description,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> Key,
    IReadOnlyList<AiContextColumn> Columns,
    IReadOnlyList<string> Deny,
    IReadOnlyList<AiContextMeasure> Measures,
    IReadOnlyList<AiContextJoin> Joins,
    int Line);

/// <summary>One column a context describes: what it means, and other words people use for it.</summary>
public sealed record AiContextColumn(string Name, string? Description, IReadOnlyList<string> Synonyms);

/// <summary>A named SQL scalar expression over its table's open columns, reused verbatim by the assistant.</summary>
public sealed record AiContextMeasure(string Name, string Expression, string? Description);

/// <summary>A join from the table it is declared on to another table, pairing columns by position.</summary>
/// <param name="To">The table joined to.</param>
/// <param name="Columns">This table's columns.</param>
/// <param name="ToColumns">The other table's columns, paired with <paramref name="Columns"/> by position.</param>
/// <param name="JoinType"><see cref="AiContextJoinTypes.Inner"/> or <see cref="AiContextJoinTypes.Left"/>.</param>
/// <param name="Description">When to use the join.</param>
public sealed record AiContextJoin(
    AiContextTableName To, IReadOnlyList<string> Columns, IReadOnlyList<string> ToColumns, string JoinType, string? Description);

/// <summary>A question the context answers, with the SQL that answers it and, optionally, the datasource it runs on.</summary>
public sealed record AiContextExample(string Question, string Sql, string? Datasource);

/// <summary>The join types a context may declare.</summary>
public static class AiContextJoinTypes
{
    /// <summary>Rows that match on both sides.</summary>
    public const string Inner = "inner";

    /// <summary>Every row of the declaring table, with the other side's columns null where nothing matches.</summary>
    public const string Left = "left";
}

/// <summary>
/// The bounds an AI context respects. Each text bound is the width of the catalog column that serves it, so a file
/// that loads always fits; the counts keep one context's cost to the assistant, and to a sync, bounded.
/// </summary>
public static class AiContextLimits
{
    /// <summary>The longest a context's name may be.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest a context's description may be.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>The longest one context's instructions may be. Instructions are read before every query, so they
    /// should be the few rules that matter, not documentation.</summary>
    public const int MaxInstructionsLength = 4000;

    /// <summary>The most tables one context may open.</summary>
    public const int MaxTables = 500;

    /// <summary>The most example questions one context may carry.</summary>
    public const int MaxExamples = 500;

    /// <summary>The longest a table's business name may be.</summary>
    public const int MaxBusinessNameLength = 200;

    /// <summary>The longest a table's description may be.</summary>
    public const int MaxTableDescriptionLength = 4000;

    /// <summary>The longest a column's description may be.</summary>
    public const int MaxColumnDescriptionLength = 2000;

    /// <summary>The most synonyms one table or column may carry. Past a few dozen, synonyms stop disambiguating a
    /// search and start matching everything.</summary>
    public const int MaxSynonyms = 30;

    /// <summary>The longest one synonym may be: a word or a short phrase, not a description.</summary>
    public const int MaxSynonymLength = 100;

    /// <summary>The stored width of a newline-joined synonym list.</summary>
    public const int MaxSynonymsStoredLength = 1000;

    /// <summary>The stored width of a newline-joined column list (a key, or one side of a join).</summary>
    public const int MaxColumnListStoredLength = 1024;

    /// <summary>The longest a measure's name may be.</summary>
    public const int MaxMeasureNameLength = 128;

    /// <summary>The longest a measure's expression may be.</summary>
    public const int MaxExpressionLength = 4000;

    /// <summary>The longest a measure's or a join's description may be.</summary>
    public const int MaxMeasureOrJoinDescriptionLength = 1000;

    /// <summary>The longest an example question may be: the width of the saved answers' question column.</summary>
    public const int MaxQuestionLength = 1000;

    /// <summary>The longest an example's SQL may be. An example is precedent the assistant reads, so it should be a
    /// query a person can check at a glance.</summary>
    public const int MaxExampleSqlLength = 20000;

    /// <summary>The longest a datasource reference may be: the width of the saved answers' reference column.</summary>
    public const int MaxDatasourceLength = 512;

    /// <summary>The longest a table, schema, database or column name may be (SQL Server's sysname).</summary>
    public const int MaxIdentifierLength = 128;
}
