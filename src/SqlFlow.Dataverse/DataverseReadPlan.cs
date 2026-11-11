using System.Globalization;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Dataverse;
using SqlFlow.Core.Model;

namespace SqlFlow.Dataverse;

/// <summary>
/// What one run reads from a Dataverse table and how: the columns with their target types, the first page's request
/// (<c>$select</c>, <c>$filter</c>, <c>$orderby</c>) and the <c>Prefer</c> header. Built from the table's metadata and the
/// flow's options by <see cref="Build"/>, a pure function, so every mapping and every refusal is testable without HTTP.
/// </summary>
internal sealed record DataverseReadPlan
{
    /// <summary>The annotation a choice, yes/no or lookup value's label rides in.</summary>
    public const string FormattedValueAnnotation = "OData.Community.Display.V1.FormattedValue";

    /// <summary>The annotation naming the table a lookup value points at.</summary>
    public const string LookupLogicalNameAnnotation = "Microsoft.Dynamics.CRM.lookuplogicalname";

    /// <summary>The longest request URL the Web API takes (32 KB); a longer one is refused with a 400.</summary>
    public const int MaxUrlLength = 32_768;

    // Money keeps four decimals and decimal ten at most, and their ranges fit these types whatever precision the column
    // is set to, so a precision change made in Power Apps never asks for a type change on the target.
    private const string MoneyType = "DECIMAL(19, 4)";
    private const string DecimalType = "DECIMAL(23, 10)";
    private const string LabelType = "NVARCHAR(4000)";
    private const string TableNameType = "NVARCHAR(128)";

    public required DataverseTableMetadata Table { get; init; }

    /// <summary>The columns the read produces, in the order they land.</summary>
    public required IReadOnlyList<DataverseColumn> Columns { get; init; }

    /// <summary>The first page's URL; later pages follow <c>@odata.nextLink</c>.</summary>
    public required Uri FirstPage { get; init; }

    /// <summary>The <c>Prefer</c> header every page request carries.</summary>
    public required string Prefer { get; init; }

    /// <summary>The row property holding each row's id, for messages.</summary>
    public string PrimaryIdProperty => Table.PrimaryIdAttribute;

    /// <summary>True when the run reads only rows past a watermark bound, so an empty answer means nothing is new.</summary>
    public bool Bounded { get; init; }

    /// <summary>False when the column list would make the request longer than the Web API takes, so the pages are asked
    /// for whole and the reader keeps only its columns from each row.</summary>
    public bool SelectsColumns { get; init; } = true;

    /// <summary>Builds the plan for <paramref name="options"/> over <paramref name="table"/>.</summary>
    /// <exception cref="SqlFlowException">A named column does not exist or cannot be read, two columns would land under one
    /// name, or the watermark column cannot bound or order the read.</exception>
    public static DataverseReadPlan Build(DataverseTableMetadata table, DataverseSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);

        var byName = new Dictionary<string, DataverseAttribute>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in table.Attributes)
        {
            byName.TryAdd(attribute.LogicalName, attribute);
        }

        if (!byName.ContainsKey(table.PrimaryIdAttribute))
        {
            throw new SqlFlowException(
                $"The Dataverse table '{table.LogicalName}' names '{table.PrimaryIdAttribute}' as its primary key, but its metadata lists no such column.");
        }

        var wanted = WantedAttributes(table, byName, options);
        var columns = new List<DataverseColumn>();
        var annotated = false;
        foreach (var attribute in wanted)
        {
            var column = ColumnOf(attribute)!;
            columns.Add(column);

            if (options.FormattedValues && HasLabel(attribute))
            {
                annotated = true;
                columns.Add(Derived(column, "name", FormattedValueAnnotation, LabelType));
            }

            if (IsPolymorphicLookup(attribute))
            {
                annotated = true;
                columns.Add(Derived(column, "type", LookupLogicalNameAnnotation, TableNameType));
            }
        }

        EnsureUniqueNames(columns, table.LogicalName);

        var order = options.OrderColumn is { } orderName ? Orderable(orderName, columns, table.LogicalName) : null;
        string? bound = null;
        if (options.WatermarkBound is { } watermark)
        {
            var column = Orderable(watermark.Column, columns, table.LogicalName);
            bound = $"{column.SelectName} gt {Literal(watermark, column)}";
        }

        var filter = (options.Filter, bound) switch
        {
            (null, null) => null,
            ({ } authored, null) => authored,
            (null, { } watermarkBound) => watermarkBound,
            ({ } authored, { } watermarkBound) => $"({authored}) and ({watermarkBound})",
        };

        // Rows come in watermark order (then by id, so equal watermarks page deterministically): the bulk load commits
        // batch by batch, and a run that fails part way must have committed the lowest watermarks so the next run's MAX
        // resumes after them. Without a watermark the id alone orders the pages.
        var orderBy = order is not null && !order.SelectName!.Equals(table.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase)
            ? $"{order.SelectName} asc,{table.PrimaryIdAttribute} asc"
            : $"{table.PrimaryIdAttribute} asc";

        // Logical names are letters, digits and underscores, and a comma is legal in a query, so the column list goes
        // unescaped: on a wide table that keeps the request well under the Web API's URL limit.
        var select = string.Join(',', columns.Where(c => c.SelectName is not null).Select(c => c.SelectName));
        var collection = new Uri(options.ApiRoot, Uri.EscapeDataString(table.EntitySetName));
        var selected = FirstPageUri(collection, select, filter, orderBy);
        var selects = selected.AbsoluteUri.Length <= MaxUrlLength;

        // A table so wide that even the bare column list passes the limit is read whole: without $select every column
        // comes back and the reader keeps its own, which costs bandwidth but loses nothing.
        var firstPage = selects ? selected : FirstPageUri(collection, select: null, filter, orderBy);
        if (firstPage.AbsoluteUri.Length > MaxUrlLength)
        {
            throw new SqlFlowException(
                $"The request for the Dataverse table '{table.LogicalName}' is longer than the {MaxUrlLength} characters the Web API takes, "
                + $"even without a column list: shorten '{DataverseSourceOptions.FilterOption}'.");
        }

        var prefer = string.Create(CultureInfo.InvariantCulture, $"odata.maxpagesize={options.PageSize}");
        if (annotated)
        {
            prefer += ",odata.include-annotations=\"*\"";
        }

        return new DataverseReadPlan
        {
            Table = table,
            Columns = columns,
            FirstPage = firstPage,
            Prefer = prefer,
            Bounded = options.WatermarkBound is not null,
            SelectsColumns = selects,
        };
    }

    private static Uri FirstPageUri(Uri collection, string? select, string? filter, string orderBy)
    {
        var query = new StringBuilder();
        if (select is not null)
        {
            query.Append("$select=").Append(select).Append('&');
        }

        if (filter is not null)
        {
            query.Append("$filter=").Append(Uri.EscapeDataString(filter)).Append('&');
        }

        query.Append("$orderby=").Append(Uri.EscapeDataString(orderBy));
        return new Uri($"{collection.AbsoluteUri}?{query}", UriKind.Absolute);
    }

    /// <summary>
    /// The column a Dataverse attribute is read as, or null when the Web API cannot return it in a row: a part of another
    /// column, a column not valid for read, and the image, file, party list and calendar rule columns whose content the
    /// service serves through requests of their own.
    /// </summary>
    internal static DataverseColumn? ColumnOf(DataverseAttribute attribute)
    {
        if (UnreadableReason(attribute) is not null)
        {
            return null;
        }

        var name = attribute.LogicalName;
        DataverseColumn Column(DataverseValueKind kind, Type clr, string sql, string? property = null) => new()
        {
            Name = name,
            Property = property ?? name,
            Kind = kind,
            ClrType = clr,
            SqlType = sql,
            SelectName = name,
            AttributeType = TypeName(attribute),
        };

        return TypeName(attribute) switch
        {
            "UniqueidentifierType" => Column(DataverseValueKind.Guid, typeof(Guid), "UNIQUEIDENTIFIER"),
            "StringType" or "MemoType" => Column(DataverseValueKind.Text, typeof(string), TextType(attribute.MaxLength)),
            "IntegerType" or "PicklistType" or "StateType" or "StatusType" => Column(DataverseValueKind.Integer, typeof(int), "INT"),
            "BigIntType" => Column(DataverseValueKind.BigInt, typeof(long), "BIGINT"),
            "DecimalType" => Column(DataverseValueKind.Decimal, typeof(decimal), DecimalType),
            "MoneyType" => Column(DataverseValueKind.Decimal, typeof(decimal), MoneyType),
            "DoubleType" => Column(DataverseValueKind.Double, typeof(double), "FLOAT"),
            "BooleanType" => Column(DataverseValueKind.Boolean, typeof(bool), "BIT"),
            "DateTimeType" => string.Equals(attribute.DateTimeBehavior, "DateOnly", StringComparison.OrdinalIgnoreCase)
                ? Column(DataverseValueKind.DateOnly, typeof(DateTime), "DATE")
                : Column(DataverseValueKind.DateTime, typeof(DateTime), "DATETIME2"),
            "EntityNameType" => Column(DataverseValueKind.Text, typeof(string), TableNameType),
            "LookupType" or "CustomerType" or "OwnerType" => Column(DataverseValueKind.Guid, typeof(Guid), "UNIQUEIDENTIFIER", $"_{name}_value"),
            "MultiSelectPicklistType" => Column(DataverseValueKind.Text, typeof(string), LabelType),
            // A type this reader has no typed mapping for (a managed property, a custom type, or one Dataverse adds later)
            // is kept as the Web API writes it rather than dropped, so a new kind of column never goes missing from the target.
            _ => Column(DataverseValueKind.Raw, typeof(string), "NVARCHAR(MAX)"),
        };
    }

    /// <summary>Why the Web API cannot return <paramref name="attribute"/> in a row, or null when it can.</summary>
    internal static string? UnreadableReason(DataverseAttribute attribute)
    {
        if (attribute.AttributeOf is not null)
        {
            return $"it is part of the column '{attribute.AttributeOf}'";
        }

        if (!attribute.IsValidForRead)
        {
            return "its metadata says it is not valid for read";
        }

        if (!attribute.IsValidODataAttribute)
        {
            return "the Web API does not expose it";
        }

        return TypeName(attribute) switch
        {
            "PartyListType" => "a party list is read through its activity parties, not as a column",
            "CalendarRulesType" => "calendar rules are read through their own table, not as a column",
            "ImageType" => "a row holds only an image column's thumbnail; the image is downloaded through a request of its own",
            "FileType" => "a row holds only a file column's id; the file is downloaded through a request of its own",
            "VirtualType" => "a virtual column has no stored value",
            _ => null,
        };
    }

    /// <summary>The column's type name (<c>StringType</c>, <c>MultiSelectPicklistType</c>, ...). Microsoft's guidance is to
    /// classify by <c>AttributeTypeName</c>, which names the types <c>AttributeType</c> lumps together as Virtual; the
    /// older property stands in only when the metadata carries no type name.</summary>
    internal static string TypeName(DataverseAttribute attribute)
        => !string.IsNullOrWhiteSpace(attribute.AttributeTypeName) ? attribute.AttributeTypeName! : attribute.AttributeType + "Type";

    private static List<DataverseAttribute> WantedAttributes(
        DataverseTableMetadata table, Dictionary<string, DataverseAttribute> byName, DataverseSourceOptions options)
    {
        var primary = byName[table.PrimaryIdAttribute];
        var required = new List<string> { primary.LogicalName };
        if (options.OrderColumn is { } order)
        {
            required.Add(order);
        }

        if (options.WatermarkBound is { } bound)
        {
            required.Add(bound.Column);
        }

        var result = new List<DataverseAttribute>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(DataverseAttribute attribute)
        {
            if (added.Add(attribute.LogicalName))
            {
                result.Add(attribute);
            }
        }

        DataverseAttribute Named(string name, string option)
        {
            if (!byName.TryGetValue(name, out var attribute))
            {
                throw new SqlFlowException(
                    $"{option} names '{name}', which the Dataverse table '{table.LogicalName}' does not have. "
                    + $"Its readable columns are: {Listing(table)}.");
            }

            if (UnreadableReason(attribute) is { } reason)
            {
                throw new SqlFlowException(
                    $"{option} names '{name}' of the Dataverse table '{table.LogicalName}', which cannot be read as a column: {reason}.");
            }

            return attribute;
        }

        // The primary key always lands first: it is the row's identity downstream, and every message names a row by it.
        Add(primary);

        if (options.Columns.Count > 0)
        {
            foreach (var name in options.Columns)
            {
                Add(Named(name, $"'{DataverseSourceOptions.ColumnsOption}'"));
            }
        }
        else
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in options.ExcludeColumns)
            {
                if (!byName.ContainsKey(name))
                {
                    throw new SqlFlowException(
                        $"'{DataverseSourceOptions.ExcludeColumnsOption}' names '{name}', which the Dataverse table '{table.LogicalName}' does not have. "
                        + $"Its readable columns are: {Listing(table)}.");
                }

                if (required.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new SqlFlowException(
                        $"'{DataverseSourceOptions.ExcludeColumnsOption}' names '{name}', which every read of the table needs "
                        + "(the primary key, or the incremental watermark column).");
                }

                excluded.Add(name);
            }

            foreach (var attribute in table.Attributes
                .Where(a => !excluded.Contains(a.LogicalName) && UnreadableReason(a) is null)
                .OrderBy(a => a.LogicalName, StringComparer.Ordinal))
            {
                Add(attribute);
            }
        }

        // The watermark column must land too: the next run's bound is the target's MAX of it.
        foreach (var name in required)
        {
            Add(Named(name, "incremental.watermarkColumn"));
        }

        return result;
    }

    private static DataverseColumn Orderable(string name, IReadOnlyList<DataverseColumn> columns, string table)
    {
        var column = columns.First(c => c.SelectName is not null && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (column.Kind is DataverseValueKind.Guid or DataverseValueKind.Boolean or DataverseValueKind.Raw)
        {
            throw new SqlFlowException(
                $"The incremental watermark column '{name}' of the Dataverse table '{table}' is a {column.AttributeType} column, "
                + "which has no order to resume from. Use versionnumber (a row version that grows with every change) or modifiedon.");
        }

        return column;
    }

    /// <summary>The OData literal of the engine's bound for <paramref name="column"/>. A date and time is floored to the
    /// whole second (the service keeps seconds), which can only widen the read; the engine still drops every row not
    /// strictly past the exact bound.</summary>
    internal static string Literal(DataverseWatermarkBound bound, DataverseColumn column)
    {
        var numeric = column.Kind is DataverseValueKind.Integer or DataverseValueKind.BigInt or DataverseValueKind.Decimal or DataverseValueKind.Double;
        var temporal = column.Kind is DataverseValueKind.DateTime or DataverseValueKind.DateOnly;
        var fits = bound.Kind switch
        {
            WatermarkKind.Whole or WatermarkKind.Fixed or WatermarkKind.Floating => numeric,
            WatermarkKind.DateTime or WatermarkKind.DateTimeOffset => temporal,
            WatermarkKind.Text => column.Kind == DataverseValueKind.Text,
            _ => false,
        };

        if (!fits)
        {
            throw new SqlFlowException(
                $"The incremental watermark column '{column.Name}' is a Dataverse {column.AttributeType} column, but the target's "
                + $"high-water mark is a {bound.Kind} value. The target column's type no longer matches the source: give it the "
                + $"type {column.SqlType}, or rebuild the target with a full load.");
        }

        switch (bound.Kind)
        {
            case WatermarkKind.Whole:
                return long.Parse(bound.CanonicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case WatermarkKind.Fixed:
                return decimal.Parse(bound.CanonicalValue, NumberStyles.Number, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case WatermarkKind.Floating:
                return double.Parse(bound.CanonicalValue, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            case WatermarkKind.DateTime:
            case WatermarkKind.DateTimeOffset:
            {
                var instant = bound.Kind == WatermarkKind.DateTime
                    ? DateTime.SpecifyKind(DateTime.Parse(bound.CanonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTimeKind.Utc)
                    : DateTimeOffset.Parse(bound.CanonicalValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime;
                var floored = new DateTime(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
                return column.Kind == DataverseValueKind.DateOnly
                    ? floored.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : floored.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            default:
                return "'" + bound.CanonicalValue.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
    }

    private static DataverseColumn Derived(DataverseColumn of, string suffix, string annotation, string sqlType) => new()
    {
        Name = of.Name + suffix,
        Property = $"{of.Property}@{annotation}",
        Kind = DataverseValueKind.Text,
        ClrType = typeof(string),
        SqlType = sqlType,
        SelectName = null,
        AttributeType = of.AttributeType,
    };

    private static void EnsureUniqueNames(IReadOnlyList<DataverseColumn> columns, string table)
    {
        var seen = new Dictionary<string, DataverseColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (seen.TryGetValue(column.Name, out var first))
            {
                var derived = column.SelectName is null ? column : first;
                throw new SqlFlowException(
                    $"The Dataverse table '{table}' has a column named '{column.Name}', and the reader would also land "
                    + $"{(derived.Property.EndsWith(LookupLogicalNameAnnotation, StringComparison.Ordinal) ? "a lookup's table" : "a label")} "
                    + $"under that name. Leave one of them out with '{DataverseSourceOptions.ExcludeColumnsOption}' or '{DataverseSourceOptions.ColumnsOption}'"
                    + $"{(derived.Property.EndsWith(FormattedValueAnnotation, StringComparison.Ordinal) ? $", or set '{DataverseSourceOptions.FormattedValuesOption}' to false" : string.Empty)}.");
            }

            seen[column.Name] = column;
        }
    }

    private static string TextType(int? maxLength)
        => maxLength is > 0 and <= 4000 ? string.Create(CultureInfo.InvariantCulture, $"NVARCHAR({maxLength.Value})") : "NVARCHAR(MAX)";

    private static bool HasLabel(DataverseAttribute attribute)
        => TypeName(attribute) is "PicklistType" or "StateType" or "StatusType" or "BooleanType" or "MultiSelectPicklistType"
            or "LookupType" or "CustomerType" or "OwnerType";

    private static bool IsPolymorphicLookup(DataverseAttribute attribute)
        => TypeName(attribute) switch
        {
            "CustomerType" or "OwnerType" => true,
            "LookupType" => attribute.Targets.Count > 1,
            _ => false,
        };

    private static string Listing(DataverseTableMetadata table)
    {
        var readable = table.Attributes.Where(a => UnreadableReason(a) is null).Select(a => a.LogicalName).Order(StringComparer.Ordinal).ToList();
        const int shown = 60;
        return readable.Count <= shown
            ? string.Join(", ", readable)
            : string.Join(", ", readable.Take(shown)) + $" and {readable.Count - shown} more";
    }
}
