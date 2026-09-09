namespace SqlFlow.Core.Catalog;

public enum ObjectSort
{
    Name,
}

/// <summary>Filtering, search, and pagination for a catalog listing. <see cref="NameLike"/> is always passed
/// as a parameter (never interpolated); LIKE wildcards inside it are escaped.</summary>
public sealed record CatalogQuery
{
    public string? NameLike { get; init; }
    public bool IncludeViews { get; init; } = true;
    public bool IncludeTables { get; init; } = true;
    public bool IncludeSystem { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 200;
    public ObjectSort Sort { get; init; } = ObjectSort.Name;

    public static readonly CatalogQuery Default = new();
}

/// <summary>A page of catalog items, with the total under the same filter so a UI can page.</summary>
public sealed record CatalogPage<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required long Total { get; init; }

    public bool HasMore => Offset + Items.Count < Total;
}

public sealed record DatabaseInfo
{
    public required string Name { get; init; }
    public string? Collation { get; init; }
    public string? State { get; init; }
}

public sealed record SchemaInfo
{
    public required string Name { get; init; }
    public string? Owner { get; init; }
}

public sealed record ObjectInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required ObjectType Type { get; init; }
    public long ApproxRows { get; init; }
}

/// <summary>The database/schema scope for listing objects; null parts mean the connection's current context
/// and all schemas.</summary>
public sealed record ObjectScope
{
    public string? Database { get; init; }
    public string? Schema { get; init; }
}

public sealed record ObjectMatch
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required ObjectType Type { get; init; }
    public int Rank { get; init; }
}
