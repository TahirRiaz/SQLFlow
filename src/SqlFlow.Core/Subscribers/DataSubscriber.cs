namespace SqlFlow.Core.Subscribers;

/// <summary>
/// One consumer of the warehouse: a Power BI report, a Tableau workbook, an Excel refresh, a notebook, an
/// application. A subscriber never runs and moves no data, so it is not a flow: it is the far end of lineage,
/// the answer to "who reads this table". The V3 form of one legacy <c>flw.DataSubscriber</c> row, minus the
/// <c>FlowID</c>/<c>FlowType</c>/<c>Batch</c> plumbing that only existed so the legacy catalog could log a row
/// per subscriber; identity here is the name, as it is for every other V3 object.
/// </summary>
public sealed record DataSubscriber
{
    /// <summary>The subscriber's name, unique across the estate (legacy <c>SubscriberName</c>). This is what the
    /// lineage graph shows as the consuming node, so it should read as the thing a person would look for: the
    /// report name, the workbook name, the application name.</summary>
    public required string Name { get; init; }

    /// <summary>What kind of consumer it is (legacy <c>SubscriberType</c>): PowerBI, Tableau, Excel, Notebook,
    /// Application, or any label the estate uses. Free text on purpose: the legacy column was a free
    /// <c>nvarchar(250)</c> fed by a lookup view, and constraining it here would reject a real subscriber.</summary>
    public required string Type { get; init; }

    /// <summary>Who owns the subscriber (legacy <c>CreatedBy</c>): the team or person to contact before a
    /// breaking change to a table it reads. Null when the estate did not record one.</summary>
    public string? Owner { get; init; }

    /// <summary>What the subscriber is for, in one line, for the catalog and the lineage node's tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>Free-form remarks about the subscriber's STATE rather than its purpose: that it has not been
    /// refreshed since a given month, that it looks superseded by another report, that it could not be opened,
    /// that a question about it is still unanswered. Kept apart from <see cref="Description"/> because the two
    /// age differently: a description is true for as long as the report exists, whereas a remark is a finding
    /// from one review of the estate and is expected to be resolved and removed. Multi-line is fine.</summary>
    public string? Notes { get; init; }

    /// <summary>Where the subscriber lives: the report URL, the workbook path, the repository. Null when there
    /// is no addressable location.</summary>
    public string? Url { get; init; }

    /// <summary>The queries the subscriber runs against the warehouse. Every one is parsed, and the objects it
    /// touches become the subscriber's lineage edges; a subscriber with no queries is a node nothing connects
    /// to, which the collector reports rather than silently accepting.</summary>
    public IReadOnlyList<SubscriberQuery> Queries { get; init; } = [];
}

/// <summary>
/// One query a subscriber runs against the warehouse: the V3 form of one legacy
/// <c>flw.DataSubscriberQuery</c> row. The <see cref="Sql"/> is parsed exactly like a stored-procedure body or
/// a document hook, so the tables and views it reads are resolved to the same node identities the loading flows
/// write, and the two ends meet in the graph.
/// </summary>
public sealed record SubscriberQuery
{
    /// <summary>A label for the query within its subscriber (legacy <c>QueryName</c>): the dataset name, the
    /// page, the measure group. Names the script in warnings and in the catalog.</summary>
    public required string Name { get; init; }

    /// <summary>The connection alias the query runs against (legacy <c>srcServer</c>, a
    /// <c>flw.SysDataSource.Alias</c>), keyed into the document's <c>connections:</c> block. This is what pins
    /// the query's two-part object names to the right server and database, so a subscriber reading
    /// <c>arc.Citybikes_Bikes</c> lands on the same node the ingestion flow writes.</summary>
    public required string Server { get; init; }

    /// <summary>The query text, as the subscriber runs it (legacy <c>FullyQualifiedQuery</c>). Any T-SQL the
    /// parser accepts: a SELECT, a set of them, or the whole dataset script a report tool emits.</summary>
    public required string Sql { get; init; }
}
