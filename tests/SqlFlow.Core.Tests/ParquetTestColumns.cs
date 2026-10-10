using System.Reflection;
using Parquet;
using Parquet.Schema;

namespace SqlFlow.Tests;

/// <summary>
/// The test fixtures' column writer over the Parquet.Net 6 typed column API, which replaced the untyped
/// <c>DataColumn</c> (field + CLR array + optional repetition levels) the fixtures were written against. A fixture
/// still hands over a field, an array and, for a repeated leaf, its repetition levels; the array's element type
/// picks the typed write: text, binary, a nullable value type (nulls in place) or a required value type.
/// <para>
/// A nested leaf (inside a list, map or struct) is written the way <c>DataColumn</c> wrote it: one array entry per
/// level slot, a value at the leaf's maximum definition level and a null one level below it. A fixture that also
/// passes definition levels hands over DENSE values (one per fully defined slot) with both level arrays, written
/// verbatim, which is how a test pins an exact empty / null / null-element layout.
/// </para>
/// </summary>
internal static class ParquetTestColumns
{
    private static readonly MethodInfo WriteNullableMethod = Method(nameof(WriteNullableAsync));
    private static readonly MethodInfo WriteRequiredMethod = Method(nameof(WriteRequiredAsync));
    private static readonly MethodInfo WriteDenseMethod = Method(nameof(WriteDenseAsync));
    private static readonly MethodInfo WriteNestedNullableMethod = Method(nameof(WriteNestedNullableAsync));

    public static Task WriteColumnAsync(
        this ParquetRowGroupWriter rowGroup, DataField field, Array values, int[]? repetitionLevels = null, int[]? definitionLevels = null)
    {
        ReadOnlyMemory<int>? levels = repetitionLevels is null ? null : new ReadOnlyMemory<int>(repetitionLevels);
        var element = values.GetType().GetElementType()
            ?? throw new ArgumentException("A Parquet test column must be a one-dimensional array.", nameof(values));

        if (definitionLevels is not null)
        {
            if (!element.IsValueType || Nullable.GetUnderlyingType(element) is not null)
            {
                throw new ArgumentException("Explicit definition levels take dense, non-nullable values.", nameof(values));
            }

            return (Task)WriteDenseMethod.MakeGenericMethod(element).Invoke(null, [rowGroup, field, values, definitionLevels, levels])!;
        }

        if (field.MaxRepetitionLevel > 0 || field.MaxDefinitionLevel > 1)
        {
            return WriteNestedAsync(rowGroup, field, values, element, levels);
        }

        switch (values)
        {
            case string[] text:
                return rowGroup.WriteAsync(field, text, levels);
            case byte[][] binary:
                return rowGroup.WriteAsync(field, binary, levels);
        }

        var underlying = Nullable.GetUnderlyingType(element);
        var write = underlying is null ? WriteRequiredMethod.MakeGenericMethod(element) : WriteNullableMethod.MakeGenericMethod(underlying);
        return (Task)write.Invoke(null, [rowGroup, field, values, levels])!;
    }

    /// <summary>A nested leaf in <c>DataColumn</c> form: each slot holds a value (the leaf's maximum definition
    /// level) or a null (one level below it); the values that are present are written densely with those levels.</summary>
    private static Task WriteNestedAsync(ParquetRowGroupWriter rowGroup, DataField field, Array values, Type element, ReadOnlyMemory<int>? levels)
    {
        var definitions = new int[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            definitions[i] = values.GetValue(i) is null ? field.MaxDefinitionLevel - 1 : field.MaxDefinitionLevel;
        }

        switch (values)
        {
            case string[] text:
            {
                var dense = text.Where(v => v is not null).Select(v => v.AsMemory()).ToArray();
                return rowGroup.WriteAllPartsAsync(field, new ReadOnlyMemory<ReadOnlyMemory<char>>(dense), definitions, levels, CancellationToken.None);
            }

            case byte[][] binary:
            {
                var dense = binary.Where(v => v is not null).Select(v => (ReadOnlyMemory<byte>)v).ToArray();
                return rowGroup.WriteAllPartsAsync(field, new ReadOnlyMemory<ReadOnlyMemory<byte>>(dense), definitions, levels, CancellationToken.None);
            }
        }

        var underlying = Nullable.GetUnderlyingType(element);
        return underlying is null
            ? (Task)WriteDenseMethod.MakeGenericMethod(element).Invoke(null, [rowGroup, field, values, definitions, levels])!
            : (Task)WriteNestedNullableMethod.MakeGenericMethod(underlying).Invoke(null, [rowGroup, field, values, definitions, levels])!;
    }

    private static Task WriteNullableAsync<T>(ParquetRowGroupWriter rowGroup, DataField field, Array values, ReadOnlyMemory<int>? levels)
        where T : struct
        => rowGroup.WriteAsync(field, new ReadOnlyMemory<T?>((T?[])values), levels);

    private static Task WriteRequiredAsync<T>(ParquetRowGroupWriter rowGroup, DataField field, Array values, ReadOnlyMemory<int>? levels)
        where T : struct
        => rowGroup.WriteAsync(field, new ReadOnlyMemory<T>((T[])values), levels);

    private static Task WriteDenseAsync<T>(
        ParquetRowGroupWriter rowGroup, DataField field, Array values, int[] definitionLevels, ReadOnlyMemory<int>? levels)
        where T : struct
        => rowGroup.WriteAllPartsAsync(field, new ReadOnlyMemory<T>((T[])values), definitionLevels, levels, CancellationToken.None);

    private static Task WriteNestedNullableAsync<T>(
        ParquetRowGroupWriter rowGroup, DataField field, Array values, int[] definitionLevels, ReadOnlyMemory<int>? levels)
        where T : struct
    {
        var dense = ((T?[])values).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return rowGroup.WriteAllPartsAsync(field, new ReadOnlyMemory<T>(dense), definitionLevels, levels, CancellationToken.None);
    }

    private static MethodInfo Method(string name)
        => typeof(ParquetTestColumns).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
}
