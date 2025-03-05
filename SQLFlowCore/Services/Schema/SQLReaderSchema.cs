using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Text;
using SQLFlowCore.Common;
using SQLFlowCore.Services.TsqlParser;
namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// The SQLReaderSchema class is responsible for handling SQL reader schemas.
    /// </summary>
    /// <remarks>
    /// This class provides methods to get the schema from an SQL reader, generate a table script from a schema, and create a map from a schema.
    /// </remarks>
    internal class SQLReaderSchema
    {
        /// <summary>
        /// Retrieves the schema from an SQL reader and returns it as a DataTable.
        /// </summary>
        /// <param name="_connectionString">The connection string to the SQL database.</param>
        /// <param name="srcCode">The source SQL code to be executed.</param>
        /// <param name="cmdParams">A list of command parameters to be used in the SQL query.</param>
        /// <returns>A DataTable representing the schema of the SQL reader.</returns>
        /// <remarks>
        /// This method modifies the source SQL code by adding "AND 1=0" to the end of all query expressions, 
        /// which ensures that no rows are returned from the query, only the schema information. 
        /// It then executes the modified SQL code and retrieves the schema from the SQL reader.
        /// Additional columns are added to the schema table to store SQL data type, SQLFlow expression, cleaned column name, and column command.
        /// </remarks>
        internal static DataTable GetSchemaFromSQLReader(string _connectionString, string srcCode, List<ParameterObject> cmdParams)
        {
            //Change the source script and add a "AND 1=0" to the end of all query expresions
            string newCode = SqlModifier.ExtendWhereClauses(srcCode, "0=1");

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                using (SqlCommand command = new SqlCommand(newCode, connection))
                {
                    command.CommandType = CommandType.Text;

                    foreach (var p in cmdParams)
                    {
                        command.Parameters.Add(p.sqlParameter);
                        /*
                        if (p.ParameterType == "MSSQL")
                        {
                        }
                        */
                    }
                    //CommandBehavior.SchemaOnly
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        DataTable schemaTable = reader.GetSchemaTable();

                        schemaTable.Columns.Add("SqlDataType", typeof(string));
                        schemaTable.Columns.Add("SQLFlowExp", typeof(string));
                        schemaTable.Columns.Add("ColumnNameCleaned", typeof(string));
                        schemaTable.Columns.Add("ColumnCMD", typeof(string));

                        foreach (DataRow row in schemaTable.Rows)
                        {
                            string columnType = DataTypeMapper.GetSqlTypeFromDotNetType((Type)row["DataType"], (int)row["ColumnSize"], (short)row["NumericPrecision"], (short)row["NumericScale"]);
                            row["SqlDataType"] = columnType;
                            row["SQLFlowExp"] = $"CAST(@ColName AS {columnType})";
                        }

                        command.Parameters.Clear();
                        reader.Close();
                        reader.Dispose();
                        connection.Close();
                        connection.Dispose();
                        return schemaTable;
                    }
                }
            }
        }


        /// <summary>
        /// Generates a SQL script to create a table based on the provided schema.
        /// </summary>
        /// <param name="schema">A DataTable representing the schema of the SQL reader.</param>
        /// <param name="targetTableName">The name of the table to be created.</param>
        /// <returns>A string representing the SQL script to create the table.</returns>
        /// <remarks>
        /// This method iterates over each row in the provided schema, mapping the column name and data type to a SQL table column definition.
        /// The SQL data type is determined by calling the GetSqlTypeFromDotNetType method from the DataTypeMapper class.
        /// The generated script includes a CREATE TABLE statement with the target table name and column definitions based on the schema.
        /// </remarks>
        internal static string GenerateTableScriptFromSchema(DataTable schema, string targetTableName)
        {
            StringBuilder sb = new StringBuilder($"CREATE TABLE {targetTableName} (\n");

            foreach (DataRow row in schema.Rows)
            {
                string columnName = row["ColumnName"]?.ToString() ?? string.Empty;
                string columnType = DataTypeMapper.GetSqlTypeFromDotNetType((Type)row["DataType"], (int)row["ColumnSize"], (short)row["NumericPrecision"], (short)row["NumericScale"]);
                sb.AppendLine($"{columnName} {columnType},");
            }

            sb.Remove(sb.Length - 3, 1); // Remove the last comma
            sb.AppendLine(")");

            return sb.ToString();
        }


        /// <summary>
        /// Creates a list of column mappings based on the provided schema.
        /// </summary>
        /// <param name="schema">A DataTable representing the schema of the SQL reader.</param>
        /// <returns>A list of SqlBulkCopyColumnMapping objects representing the column mappings.</returns>
        /// <remarks>
        /// This method iterates over each row in the provided schema, creating a new SqlBulkCopyColumnMapping for each column.
        /// The column name in the schema is used for both the source and destination column names in the mapping.
        /// </remarks>
        internal static List<SqlBulkCopyColumnMapping> CreateMap(DataTable schema)
        {
            var bulkCopyMappings = new List<SqlBulkCopyColumnMapping>();

            foreach (DataRow row in schema.Rows)
            {
                string columnName = row["ColumnName"]?.ToString() ?? string.Empty;
                bulkCopyMappings.Add(new SqlBulkCopyColumnMapping(columnName, columnName));
            }

            return bulkCopyMappings;
        }

    }


}
