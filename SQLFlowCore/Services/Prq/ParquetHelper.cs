using Parquet;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Parquet.Data.Ado;
using SQLFlowCore.Common;
using TorchSharp.Modules;
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
        internal static string GenerateCreateTableSql(ParquetDataReader dataReader, string threePartTableName, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            if (dataReader == null)
            {
                throw new ArgumentNullException(nameof(dataReader));
            }

            var columnsSql = BuildColumnsSql(dataReader, virtualColumns, defaultColDataType);
            return $"CREATE TABLE {threePartTableName} ({string.Join(", ", columnsSql)});";
        }

        /// <summary>
        /// Builds a list of SQL column definitions based on the schema of a Parquet file and a list of virtual columns.
        /// </summary>
        /// <param name="reader">The ParquetReader used to read the Parquet file.</param>
        /// <param name="virtualColumns">A list of virtual columns to be added to the table.</param>
        /// <param name="defaultColDataType">The default data type to be used for the columns.</param>
        /// <returns>A list of strings, where each string is a SQL column definition.</returns>
        private static List<string> BuildColumnsSql(ParquetDataReader dataReader, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            var columnsSql = new List<string>();
            var schema = GetParquetColumns(dataReader, virtualColumns, defaultColDataType);
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
        internal static SortedList<int, Tuple<string, string>> GetSQLColumnsWithDT(ParquetDataReader dataReader, string defaultColDataType)
        {
            if (dataReader == null) throw new ArgumentNullException(nameof(dataReader));

            SortedList<int, Tuple<string, string>> col = new SortedList<int, Tuple<string, string>>();
            DataTable schemaTable = dataReader.GetSchemaTable();

            foreach (DataRow row in schemaTable.Rows)
            {
                string columnName = row[SchemaTableColumn.ColumnName].ToString();
                int ordinal = Convert.ToInt32(row[SchemaTableColumn.ColumnOrdinal]);
                Type dataType = (Type)row[SchemaTableColumn.DataType];

                Tuple<string, string> tp = new Tuple<string, string>(
                    $"[{columnName}]",
                    DataTypeMapper.GetSqlServerDataType(dataType, defaultColDataType)
                );

                col.Add(ordinal, tp);
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
        /// Retrieves the schema of columns from a Parquet data source, including both physical and virtual columns.
        /// </summary>
        /// <param name="dataReader">
        /// The <see cref="ParquetDataReader"/> instance used to read the schema of physical columns from the Parquet file.
        /// </param>
        /// <param name="virtualColumns">
        /// A sorted list of virtual columns where the key represents the column ordinal, and the value is a <see cref="DataColumn"/> object.
        /// </param>
        /// <param name="defaultColDataType">
        /// The default data type to be used for columns when no specific data type is provided.
        /// </param>
        /// <returns>
        /// A <see cref="DataTable"/> containing the combined schema of physical and virtual columns, including metadata such as column names, ordinals, and data types.
        /// </returns>
        /// <remarks>
        /// This method processes the schema from the provided <paramref name="dataReader"/> and enriches it with virtual columns.
        /// It ensures that all columns, whether physical or virtual, are included in the resulting schema table.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// Thrown if <paramref name="dataReader"/> or <paramref name="virtualColumns"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown if the schema table cannot be retrieved or processed correctly.
        /// </exception>
        internal static DataTable GetParquetColumns(ParquetDataReader dataReader, SortedList<int, DataColumn> virtualColumns, string defaultColDataType)
        {
            DataTable schemaTable = GetSchemaTable();

            // Get the base schema table from ParquetDataReader
            DataTable baseSchemaTable = dataReader.GetSchemaTable();

            // Process physical columns from the ParquetDataReader's schema
            for (int i = 0; i < baseSchemaTable.Rows.Count; i++)
            {
                DataRow sourceRow = baseSchemaTable.Rows[i];
                string columnName = sourceRow[SchemaTableColumn.ColumnName].ToString();
                int ordinal = Convert.ToInt32(sourceRow[SchemaTableColumn.ColumnOrdinal]);
                Type dataType = (Type)sourceRow[SchemaTableColumn.DataType];

                AddColumnToSchemaTable(
                    schemaTable,
                    columnName,
                    ordinal,
                    dataType,
                    defaultColDataType);
            }

            // Process virtual columns
            foreach (var kvp in virtualColumns)
            {
                AddColumnToSchemaTable(
                    schemaTable,
                    kvp.Value.ColumnName,
                    kvp.Key,
                    kvp.Value.DataType,
                    defaultColDataType);
            }

            return schemaTable;
        }

        private static void AddColumnToSchemaTable(
            DataTable schemaTable,
            string columnName,
            int ordinal,
            Type dataType,
            string defaultColDataType)
        {
            DataRow row = schemaTable.NewRow();
            string sqlDataType = DataTypeMapper.GetSqlServerDataType(dataType, defaultColDataType);

            row[SchemaTableColumn.ColumnName] = columnName;
            row[SchemaTableColumn.ColumnOrdinal] = ordinal;
            row[SchemaTableColumn.ColumnSize] = -1; // Unknown size
            row[SchemaTableColumn.DataType] = dataType;
            row[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
            row[SchemaTableColumn.IsUnique] = false; // Assume not unique
            row[SchemaTableColumn.IsKey] = false; // Assume not primary key
            row["SqlDataType"] = sqlDataType;
            row["SQLFlowExp"] = $"CAST(@ColName AS {sqlDataType})";

            schemaTable.Rows.Add(row);
        }


        internal static DataTable GetParquetColumns(ParquetDataReader dataReader, string defaultColDataType)
        {
            // Get the schema table from the data reader
            DataTable baseSchemaTable = dataReader.GetSchemaTable();
            DataTable schemaTable = GetSchemaTable(); // Your custom schema table

            for (int i = 0; i < baseSchemaTable.Rows.Count; i++)
            {
                DataRow sourceRow = baseSchemaTable.Rows[i];
                DataRow row = schemaTable.NewRow();

                row[SchemaTableColumn.ColumnName] = sourceRow[SchemaTableColumn.ColumnName];
                row[SchemaTableColumn.ColumnOrdinal] = sourceRow[SchemaTableColumn.ColumnOrdinal];
                row[SchemaTableColumn.ColumnSize] = -1; // Unknown size
                row[SchemaTableColumn.DataType] = sourceRow[SchemaTableColumn.DataType];
                row[SchemaTableColumn.AllowDBNull] = true; // Assume nullable
                row[SchemaTableColumn.IsUnique] = false; // Assume not unique
                row[SchemaTableColumn.IsKey] = false; // Assume not primary key

                Type clrType = (Type)sourceRow[SchemaTableColumn.DataType];
                row["SqlDataType"] = DataTypeMapper.GetSqlServerDataType(clrType, defaultColDataType);
                row["SQLFlowExp"] = $"CAST(@ColName AS {DataTypeMapper.GetSqlServerDataType(clrType, defaultColDataType)})";

                schemaTable.Rows.Add(row);
            }
            return schemaTable;
        }

    }

}