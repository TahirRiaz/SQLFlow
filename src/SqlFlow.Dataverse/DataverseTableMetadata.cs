namespace SqlFlow.Dataverse;

/// <summary>What the reader needs to know about a Dataverse table, read from its metadata on every run.</summary>
internal sealed record DataverseTableMetadata
{
    /// <summary>The table's logical name (<c>account</c>).</summary>
    public required string LogicalName { get; init; }

    /// <summary>The Web API collection the rows are read from (<c>accounts</c>).</summary>
    public required string EntitySetName { get; init; }

    /// <summary>The logical name of the table's primary key column (<c>accountid</c>).</summary>
    public required string PrimaryIdAttribute { get; init; }

    /// <summary>Every column of the table, in the order the metadata listed them.</summary>
    public required IReadOnlyList<DataverseAttribute> Attributes { get; init; }
}

/// <summary>One column of a Dataverse table, as its attribute metadata describes it.</summary>
internal sealed record DataverseAttribute
{
    public required string LogicalName { get; init; }

    /// <summary>The attribute type (<c>String</c>, <c>Lookup</c>, <c>DateTime</c>, ...), as <c>AttributeType</c> names it.</summary>
    public required string AttributeType { get; init; }

    /// <summary>The attribute type name (<c>StringType</c>, <c>MultiSelectPicklistType</c>, ...), which tells apart the
    /// types <see cref="AttributeType"/> lumps together as <c>Virtual</c>.</summary>
    public string? AttributeTypeName { get; init; }

    public bool IsValidForRead { get; init; }

    public bool IsValidODataAttribute { get; init; }

    /// <summary>The column this one is a part of (a lookup's name or type companion), or null for a column of its own.</summary>
    public string? AttributeOf { get; init; }

    /// <summary>A text column's length in characters, or null when the metadata gives none.</summary>
    public int? MaxLength { get; init; }

    /// <summary>A date and time column's behaviour (<c>UserLocal</c>, <c>DateOnly</c>, <c>TimeZoneIndependent</c>), or null.</summary>
    public string? DateTimeBehavior { get; init; }

    /// <summary>The tables a lookup column may point at; more than one makes it polymorphic.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];
}
