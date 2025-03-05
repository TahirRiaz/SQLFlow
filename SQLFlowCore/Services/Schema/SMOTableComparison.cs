using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.Management.Smo;

internal class SMOTableComparison
{
    private Dictionary<string, ColumnMetadata> sourceColumnMetadata;
    private Dictionary<string, ColumnMetadata> targetColumnMetadata;

    public string AddMissingElements(Table sourceTable, Table targetTable)
    {
        if (sourceTable == null || targetTable == null)
        {
            throw new ArgumentNullException(sourceTable == null ? nameof(sourceTable) : nameof(targetTable));
        }

        PreloadMetadata(sourceTable, targetTable);

        StringBuilder deltaScript = new StringBuilder();
        AddMissingColumns(sourceTable, targetTable, deltaScript);
        return deltaScript.ToString();
    }

    private void PreloadMetadata(Table sourceTable, Table targetTable)
    {
        sourceColumnMetadata = sourceTable.Columns.Cast<Column>().ToDictionary(
            c => c.Name,
            c => new ColumnMetadata(c)
        );

        targetColumnMetadata = targetTable.Columns.Cast<Column>().ToDictionary(
            c => c.Name,
            c => new ColumnMetadata(c)
        );
    }

    private void AddMissingColumns(Table sourceTable, Table targetTable, StringBuilder deltaScript)
    {
        foreach (var sourceColumnKvp in sourceColumnMetadata)
        {
            string columnName = sourceColumnKvp.Key;
            ColumnMetadata sourceColumnMeta = sourceColumnKvp.Value;

            if (!targetColumnMetadata.TryGetValue(columnName, out ColumnMetadata targetColumnMeta))
            {
                // Add new column
                deltaScript.AppendLine($"ALTER TABLE [{targetTable.Schema}].[{targetTable.Name}] ADD {GenerateColumnDefinition(sourceColumnMeta)};");
            }
            else if (!CompareDataTypes(sourceColumnMeta.DataType, targetColumnMeta.DataType))
            {
                // Alter the column data type
                deltaScript.AppendLine($"ALTER TABLE [{targetTable.Schema}].[{targetTable.Name}] ALTER COLUMN {GenerateColumnDefinition(sourceColumnMeta)};");
            }
        }
    }

    private static string GenerateColumnDefinition(ColumnMetadata columnMeta)
    {
        StringBuilder columnDef = new StringBuilder();
        columnDef.Append($"[{columnMeta.Name}] {GetFullDataType(columnMeta)}");

        if (columnMeta.IsNullable.HasValue)
        {
            columnDef.Append(columnMeta.IsNullable.Value ? " NULL" : " NOT NULL");
        }

        if (columnMeta.IsIdentity)
        {
            columnDef.Append($" IDENTITY({columnMeta.IdentitySeed},{columnMeta.IdentityIncrement})");
        }

        //if (!string.IsNullOrEmpty(columnMeta.Default))
        //{
        //    columnDef.Append($" CONSTRAINT [DF_{columnMeta.TableName}_{columnMeta.Name}] DEFAULT {columnMeta.Default}");
        //}

        if (columnMeta.IsComputed)
        {
            string persistedKeyword = columnMeta.IsPersisted ? "PERSISTED" : "";
            columnDef.Append($" AS ({columnMeta.ComputedText}) {persistedKeyword}");
        }

        return columnDef.ToString();
    }

    private static bool CompareDataTypes(DataTypeMetadata source, DataTypeMetadata target)
    {
        if (source.SqlDataType != target.SqlDataType) return false;

        switch (source.SqlDataType)
        {
            case SqlDataType.Char:
            case SqlDataType.VarChar:
            case SqlDataType.Binary:
            case SqlDataType.VarBinary:
            case SqlDataType.NChar:
            case SqlDataType.NVarChar:
            case SqlDataType.NVarCharMax:
            case SqlDataType.VarCharMax:
                return source.MaximumLength == target.MaximumLength;

            case SqlDataType.Decimal:
            case SqlDataType.Numeric:
                return source.NumericPrecision == target.NumericPrecision &&
                       source.NumericScale == target.NumericScale;

            case SqlDataType.DateTime2:
            case SqlDataType.DateTimeOffset:
            case SqlDataType.Time:
                return source.NumericScale == target.NumericScale;

            default:
                return true;
        }
    }

    private static string GetFullDataType(ColumnMetadata columnMeta)
    {
        string dataType = columnMeta.DataType.Name;

        switch (columnMeta.DataType.SqlDataType)
        {
            case SqlDataType.Char:
            case SqlDataType.VarChar:
            case SqlDataType.Binary:
            case SqlDataType.VarBinary:
            case SqlDataType.NChar:
            case SqlDataType.NVarChar:
                if (columnMeta.DataType.MaximumLength == -1)
                {
                    dataType += "(max)";
                }
                else
                {
                    int length = columnMeta.DataType.SqlDataType == SqlDataType.NChar || columnMeta.DataType.SqlDataType == SqlDataType.NVarChar
                        ? columnMeta.DataType.MaximumLength / 2
                        : columnMeta.DataType.MaximumLength;
                    dataType += $"({length})";
                }
                break;

            case SqlDataType.Decimal:
            case SqlDataType.Numeric:
                dataType += $"({columnMeta.DataType.NumericPrecision}, {columnMeta.DataType.NumericScale})";
                break;

            case SqlDataType.DateTime2:
            case SqlDataType.DateTimeOffset:
            case SqlDataType.Time:
                if (columnMeta.DataType.NumericScale > 0)
                {
                    dataType += $"({columnMeta.DataType.NumericScale})";
                }
                break;

            case SqlDataType.VarBinaryMax:
            case SqlDataType.VarCharMax:
            case SqlDataType.NVarCharMax:
            case SqlDataType.Xml:
                dataType += "(max)";
                break;
        }

        return dataType;
    }
}

internal class ColumnMetadata
{
    public string Name { get; set; }
    public string TableName { get; set; }
    public DataTypeMetadata DataType { get; set; }
    public bool? IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public long? IdentitySeed { get; set; }
    public long? IdentityIncrement { get; set; }
    public string Default { get; set; }
    public bool IsComputed { get; set; }
    public string ComputedText { get; set; }
    public bool IsPersisted { get; set; }

    public ColumnMetadata(Column column)
    {
        Name = column.Name;
        //TableName = (column.Parent as Table)?.Name;
        DataType = new DataTypeMetadata(column.DataType);
        //IsNullable = GetPropertySafely(column, c => c.Nullable);
        //IsIdentity = GetPropertySafely(column, c => c.Identity);
        //IdentitySeed = GetPropertySafely(column, c => c.IdentitySeed);
        //IdentityIncrement = GetPropertySafely(column, c => c.IdentityIncrement);
        //Default = GetPropertySafely(column, c => c.Default);
        //IsComputed = GetPropertySafely(column, c => c.Computed);
        //ComputedText = GetPropertySafely(column, c => c.ComputedText);
        //IsPersisted = GetPropertySafely(column, c => c.IsPersisted);
    }

    private static T GetPropertySafely<T>(Column column, Func<Column, T> propertyAccessor)
    {
        try
        {
            return propertyAccessor(column);
        }
        catch (PropertyNotSetException)
        {
            return default(T);
        }
    }
}

internal class DataTypeMetadata
{
    public SqlDataType SqlDataType { get; set; }
    public string Name { get; set; }
    public int MaximumLength { get; set; }
    public int NumericPrecision { get; set; }
    public int NumericScale { get; set; }

    public DataTypeMetadata(DataType dataType)
    {
        SqlDataType = dataType.SqlDataType;
        Name = dataType.Name;
        MaximumLength = GetPropertySafely(dataType, c => c.MaximumLength);
        NumericPrecision = GetPropertySafely(dataType,c => c.NumericPrecision);
        NumericScale = GetPropertySafely(dataType, c => c.NumericScale);
    }

    private static T GetPropertySafely<T>(DataType dataType, Func<DataType, T> propertyAccessor)
    {
        try
        {
            return propertyAccessor(dataType);
        }
        catch (PropertyNotSetException)
        {
            return default(T);
        }
    }
}

