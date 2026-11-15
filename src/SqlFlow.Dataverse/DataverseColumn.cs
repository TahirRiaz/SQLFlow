namespace SqlFlow.Dataverse;

/// <summary>How a Dataverse value is carried from the Web API's JSON into a typed cell.</summary>
internal enum DataverseValueKind
{
    /// <summary>A GUID string: the primary key, a lookup's id, any uniqueidentifier column.</summary>
    Guid,

    /// <summary>Text: single and multiple lines of text, a multi-select choice's comma-separated values, a table name, a label.</summary>
    Text,

    /// <summary>A whole number that fits 32 bits: whole number columns and the value of a choice, status or state.</summary>
    Integer,

    /// <summary>A whole number that needs 64 bits: big integer columns such as versionnumber.</summary>
    BigInt,

    /// <summary>A fixed-point number: decimal and currency columns.</summary>
    Decimal,

    /// <summary>A floating-point number.</summary>
    Double,

    /// <summary>A yes/no column.</summary>
    Boolean,

    /// <summary>A date and time, returned in UTC and kept in UTC.</summary>
    DateTime,

    /// <summary>A date-only column (DateOnly behaviour): a calendar date with no time zone.</summary>
    DateOnly,

    /// <summary>A column type this reader has no typed mapping for: the value as the Web API wrote it, as text.</summary>
    Raw,
}

/// <summary>
/// One column a Dataverse read produces: the name it lands under, the JSON property each row carries it in, how its
/// value converts, and the SQL type the target gets. A column read from the table names its logical name in
/// <see cref="SelectName"/>; a label or lookup-table column derived from an annotation has none.
/// </summary>
internal sealed record DataverseColumn
{
    /// <summary>The target column's name: the logical name, or the logical name plus <c>name</c>/<c>type</c> for a derived column.</summary>
    public required string Name { get; init; }

    /// <summary>The property each row carries the value in (<c>_parentaccountid_value</c> for a lookup, an annotation for a label).</summary>
    public required string Property { get; init; }

    public required DataverseValueKind Kind { get; init; }

    /// <summary>The CLR type of the cell the reader yields.</summary>
    public required Type ClrType { get; init; }

    /// <summary>The SQL Server type the target column is created or widened to.</summary>
    public required string SqlType { get; init; }

    /// <summary>The logical name named in <c>$select</c>, or null for a column derived from another column's annotation.</summary>
    public string? SelectName { get; init; }

    /// <summary>The Dataverse attribute type (<c>DateTime</c>, <c>BigInt</c>, ...) the column was read from, for messages.</summary>
    public required string AttributeType { get; init; }
}
