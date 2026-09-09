using System.Text.Json.Serialization;

namespace SqlFlow.Core.Translate;

/// <summary>
/// One node of a translation flow's declared JSON shape. The template mirrors the target document: a YAML mapping
/// without directive keys is an object emitted verbatim, a YAML sequence is a fixed-length array, and a leaf is a
/// constant, a column reference, or a templated string. Directive keys are <c>$</c>-prefixed (<c>$column</c>,
/// <c>$value</c>, <c>$template</c>, <c>$forEach</c>, ...) precisely so the template can express ANY property name a
/// target schema uses (GeoJSON's <c>type</c>, OSDU's <c>kind</c>) without colliding with the template dialect.
/// Immutable; compiled once at parse time and rendered once per document.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$node")]
[JsonDerivedType(typeof(TranslateObjectNode), "object")]
[JsonDerivedType(typeof(TranslateListNode), "list")]
[JsonDerivedType(typeof(TranslateArrayNode), "array")]
[JsonDerivedType(typeof(TranslateRowNode), "row")]
[JsonDerivedType(typeof(TranslateValueNode), "value")]
public abstract record TranslateNode;

/// <summary>One named property of an object node, in declaration order (JSON object member order is preserved).</summary>
public sealed record TranslateProperty(string Name, TranslateNode Value);

/// <summary>A JSON object whose property names are emitted exactly as authored.</summary>
public sealed record TranslateObjectNode(IReadOnlyList<TranslateProperty> Properties) : TranslateNode;

/// <summary>A fixed-length JSON array authored as a YAML sequence: each element is its own template node.</summary>
public sealed record TranslateListNode(IReadOnlyList<TranslateNode> Items) : TranslateNode;

/// <summary>
/// A data-driven JSON array (<c>$forEach</c> + <c>$item</c>): one element per row of the named dataset whose bind
/// columns match the enclosing scope (or per primary row for the reserved dataset <c>rows</c>). The item template
/// renders with that row pushed onto the scope, so inner leaves see the row's columns first and the enclosing
/// rows' columns behind them.
/// </summary>
public sealed record TranslateArrayNode(string ForEach, TranslateNode Item) : TranslateNode;

/// <summary>
/// A single-row block (<c>$row</c> + <c>$item</c>): the header/one-to-one counterpart of a repeater. The named
/// dataset must resolve to EXACTLY one row at the enclosing scope (a bind-less header dataset, a one-to-one
/// child keyed by its bind columns, or the reserved <c>rows</c> when the primary result is one row); the item
/// renders with that row pushed onto the scope. Zero or several matching rows fail the run with the count, so
/// a broken header query can never silently emit a wrong document.
/// </summary>
public sealed record TranslateRowNode(string Row, TranslateNode Item) : TranslateNode;

/// <summary>How a leaf value is produced.</summary>
public enum TranslateValueSource
{
    /// <summary>A source column referenced by name (<c>$column</c>, or the whole-string token form <c>"{Col}"</c>).</summary>
    Column,

    /// <summary>A constant carried verbatim from the YAML (<c>$value</c>, or a scalar with no tokens).</summary>
    Constant,

    /// <summary>A string built from literal text and <c>{Column}</c> tokens (<c>$template</c>, or a scalar with tokens).</summary>
    Template,
}

/// <summary>The JSON type a leaf coerces its value to.</summary>
// The member names ARE the YAML dialect's $type tokens (string, int, long, double, decimal, ...), matched by
// name at parse time, so they are an authored-surface contract, not free identifiers; CA1720's rename would
// change the YAML language.
#pragma warning disable CA1720
public enum TranslateValueType
{
    /// <summary>Map the source value natively: numbers stay numbers, booleans stay booleans, strings stay strings,
    /// date/time values become ISO 8601 strings, binary becomes base64. The default.</summary>
    Auto,

    String,

    Int,

    Long,

    Double,

    Decimal,

    Bool,

    /// <summary>A date-only value, formatted <c>yyyy-MM-dd</c> unless the leaf declares a format.</summary>
    Date,

    /// <summary>A date-time value, formatted ISO 8601 unless the leaf declares a format.</summary>
    DateTime,

    /// <summary>The source string IS json: parse it and embed the parsed structure (a GeoJSON column, a stored
    /// document fragment) instead of emitting it as an escaped string.</summary>
    Json,
}
#pragma warning restore CA1720

/// <summary>What a leaf emits when its source value is NULL (or a template references only NULL columns).</summary>
public enum TranslateNullPolicy
{
    /// <summary>Follow the document-level <c>documents.nulls</c> setting. The default.</summary>
    Inherit,

    /// <summary>Leave the property out of the object entirely.</summary>
    Omit,

    /// <summary>Emit an explicit JSON null.</summary>
    Null,

    /// <summary>Emit the leaf's <c>$default</c> constant.</summary>
    Default,
}

/// <summary>
/// A leaf of the template: exactly one source (column, constant, or templated string), an optional type coercion
/// with format, and the null policy. Constants are carried as canonical JSON text (<see cref="ConstantJson"/>)
/// so a <c>$value</c> may be any JSON shape, scalar or structured, and each rendered document materializes its
/// own fresh instance.
/// </summary>
public sealed record TranslateValueNode
    : TranslateNode
{
    public required TranslateValueSource Source { get; init; }

    /// <summary>The source column name, for <see cref="TranslateValueSource.Column"/>.</summary>
    public string? Column { get; init; }

    /// <summary>The constant as canonical JSON text, for <see cref="TranslateValueSource.Constant"/> (and for the
    /// <see cref="TranslateNullPolicy.Default"/> fallback via <see cref="DefaultJson"/>).</summary>
    public string? ConstantJson { get; init; }

    /// <summary>The template text with <c>{Column}</c> tokens, for <see cref="TranslateValueSource.Template"/>.
    /// <c>{{</c>/<c>}}</c> escape literal braces; <c>${...}</c> sequences pass through verbatim.</summary>
    public string? Template { get; init; }

    public TranslateValueType Type { get; init; } = TranslateValueType.Auto;

    /// <summary>An explicit .NET format string for the date/date-time types (invariant culture).</summary>
    public string? Format { get; init; }

    public TranslateNullPolicy WhenNull { get; init; } = TranslateNullPolicy.Inherit;

    /// <summary>The <c>$default</c> constant as canonical JSON text, emitted under <see cref="TranslateNullPolicy.Default"/>.</summary>
    public string? DefaultJson { get; init; }
}
