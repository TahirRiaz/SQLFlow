using Parquet;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using SQLFlowCore.Common;
namespace SQLFlowCore.Services.Prq
{
    /// <summary>
    /// Provides helper methods for handling Parquet files.
    /// </summary>
    internal class ParquetHelper
    {
        /// <summary>
        /// Generates a SQL command to create a table based on the schema of a Parquet file.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="threePartTableName">The name of the table to be created.</param>
        /// <param name="virtualColumns">A list of virtual columns to be added to the table.</param>
        /// <returns>A string containing the SQL command.</returns>
        internal static string GenerateCreateTableSql(ParquetReader reader, string threePartTableName, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }
            var columnsSql = BuildColumnsSql(reader, virtualColumns, defaultColDataType);
            return $"CREATE TABLE {threePartTableName} ({string.Join(", ", columnsSql)});";
        }

        /// <summary>
        /// Builds a list of SQL column definitions based on the schema of a Parquet file and a list of virtual columns.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="virtualColumns">A list of virtual columns to be added to the table.</param>
        /// <param name="defaultColDataType">The default data type to be used for the columns.</param>
        /// <returns>A list of strings, where each string is a SQL column definition.</returns>
        private static List<string> BuildColumnsSql(ParquetReader reader, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            var columnsSql = new List<string>();
            var schema = GetParquetColumns(reader, virtualColumns, defaultColDataType);
            foreach (DataRow row in schema.Rows)
            {
                var columnSql = BuildColumnSql(row, defaultColDataType);
                columnsSql.Add(columnSql);
            }
            return columnsSql;
        }

        /// <summary>
        /// Builds the SQL for a column in a table.
        /// </summary>
        /// <param name="row">The DataRow that contains the column information.</param>
        /// <param name="defaultColDataType">The default data type to be used if the column's data type is not recognized.</param>
        /// <returns>A string that represents the SQL for the column.</returns>
        private static string BuildColumnSql(DataRow row, string defaultColDataType)
        {
            var columnName = row["ColumnName"]?.ToString() ?? string.Empty;
            var dataType = (Type)row["DataType"];
            var sqlType = DataTypeMapper.GetSqlServerDataType(dataType, defaultColDataType);
            return $"[{columnName}] {sqlType} NULL";
        }

        /// <summary>
        /// Retrieves the SQL columns along with their data types from a Parquet file.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="defaultColDataType">The default data type to be used for the columns.</param>
        /// <returns>A SortedList where the key is the column index and the value is a Tuple containing the column name and its data type.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the provided ParquetReader is null.</exception>
        internal static SortedList<int, Tuple<string, string>> GetSQLColumnsWithDT(ParquetReader reader, string defaultColDataType)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            SortedList<int, Tuple<string, string>> col = new SortedList<int, Tuple<string, string>>();

            int colCounter = 0;
            foreach (var field in reader.Schema.DataFields)
            {
                Tuple<string, string> tp = new Tuple<string, string>($"[{field.Name}]", DataTypeMapper.GetSqlServerDataType(field.ClrType, defaultColDataType));
                col.Add(colCounter, tp);
                colCounter++;
            }

            return col;
        }

        /// <summary>
        /// Creates a new DataTable that represents the schema of a table.
        /// </summary>
        /// <returns>
        /// A DataTable that contains the schema information for a table.
        /// </returns>
        /// <remarks>
        /// The schema information includes column name, ordinal, size, data type, nullability, uniqueness, key status, SQL data type, SQLFlow expression, cleaned column name, and column command.
        /// </remarks>
        private static DataTable GetSchemaTable()
        {
            DataTable schemaTable = new DataTable("SchemaTable");
            schemaTable.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
            schemaTable.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
            schemaTable.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
            schemaTable.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
            schemaTable.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
            //schemaTable.Columns.Add(SchemaTableColumn.read, typeof(bool)));
            schemaTable.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
            schemaTable.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
            schemaTable.Columns.Add("SqlDataType", typeof(string));
            schemaTable.Columns.Add("SQLFlowExp", typeof(string));
            schemaTable.Columns.Add("ColumnNameCleaned", typeof(string));
            schemaTable.Columns.Add("ColumnCMD", typeof(string));

            return schemaTable;
        }

        /// <summary>
        /// Retrieves the columns from a Parquet file and maps them to a DataTable.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="virtualColumns">A SortedList of virtual columns to be added to the DataTable.</param>
        /// <param name="defaultColDataType">The default data type to be used for the columns.</param>
        /// <returns>A DataTable representing the schema of the Parquet file, including virtual columns.</returns>
        internal static DataTable GetParquetColumns(ParquetReader reader, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            DataTable schemaTable = GetSchemaTable();

            for (int i = 0; i < reader.Schema.Fields.Count; i++)
            {
                DataRow row = schemaTable.NewRow();
                row[SchemaTableColumn.ColumnName] = reader.Schema.Fields[i].Name;
                row[SchemaTableColumn.ColumnOrdinal] = i;
                row[SchemaTableColumn.ColumnSize] = -1; // Unknown size
                row[SchemaTableColumn.DataType] = reader.Schema.DataFields[i].ClrType;
                row[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
                //row[SchemaTableColumn.IsReadOnly] = true; // ReadOnly from data source
                row[SchemaTableColumn.IsUnique] = false; // Assume not unique
                row[SchemaTableColumn.IsKey] = false; // Assume not primary key
                row["SqlDataType"] = DataTypeMapper.GetSqlServerDataType(reader.Schema.DataFields[i].ClrType, defaultColDataType);
                row["SQLFlowExp"] = $"CAST(@ColName AS {DataTypeMapper.GetSqlServerDataType(reader.Schema.DataFields[i].ClrType, defaultColDataType)})";
                schemaTable.Rows.Add(row);
            }

            foreach (var item in virtualColumns)
            {
                DataColumn dc = item.Value;
                DataRow rowx = schemaTable.NewRow();
                rowx[SchemaTableColumn.ColumnName] = dc.ColumnName;
                rowx[SchemaTableColumn.ColumnOrdinal] = item.Key;
                rowx[SchemaTableColumn.ColumnSize] = -1; // Unknown size
                rowx[SchemaTableColumn.DataType] = dc.DataType;
                rowx[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
                rowx[SchemaTableColumn.IsUnique] = false; // Assume not unique
                rowx[SchemaTableColumn.IsKey] = false; // Assume not primary key
                rowx["SqlDataType"] = DataTypeMapper.GetSqlServerDataType(dc.DataType, defaultColDataType);
                rowx["SQLFlowExp"] = $"CAST(@ColName AS {DataTypeMapper.GetSqlServerDataType(dc.DataType, defaultColDataType)})";
                schemaTable.Rows.Add(rowx);
            }

            return schemaTable;
        }

        /// <summary>
        /// Retrieves the columns from a Parquet file and maps them to a DataTable.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="defaultColDataType">The default data type to be used for the columns.</param>
        /// <returns>A DataTable representing the schema of the Parquet file.</returns>
        internal static DataTable GetParquetColumns(ParquetReader reader, string defaultColDataType)
        {
            DataTable schemaTable = GetSchemaTable();

            for (int i = 0; i < reader.Schema.Fields.Count; i++)
            {
                DataRow row = schemaTable.NewRow();
                row[SchemaTableColumn.ColumnName] = reader.Schema.Fields[i].Name;
                row[SchemaTableColumn.ColumnOrdinal] = i;
                row[SchemaTableColumn.ColumnSize] = -1; // Unknown size
                row[SchemaTableColumn.DataType] = reader.Schema.DataFields[i].ClrType;
                row[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
                //row[SchemaTableColumn.IsReadOnly] = true; // ReadOnly from data source
                row[SchemaTableColumn.IsUnique] = false; // Assume not unique
                row[SchemaTableColumn.IsKey] = false; // Assume not primary key
                row["SqlDataType"] = DataTypeMapper.GetSqlServerDataType(reader.Schema.DataFields[i].ClrType, defaultColDataType);
                row["SQLFlowExp"] = $"CAST(@ColName AS {DataTypeMapper.GetSqlServerDataType(reader.Schema.DataFields[i].ClrType, defaultColDataType)})";
                schemaTable.Rows.Add(row);
            }

            return schemaTable;
        }

    }

}