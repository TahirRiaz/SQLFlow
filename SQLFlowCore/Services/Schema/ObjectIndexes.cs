using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SQLFlowCore.Common;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Index = Microsoft.SqlServer.Management.Smo.Index;

namespace SQLFlowCore.Services.Schema
{
    internal class ObjectIndexes
    {
        public static string GetObjectIndexes(string connectionString, string database, string schema, string objectName, bool includeIfNotExists = false)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                var scripts = new StringBuilder();
                try
                {
                    connection.Open();
                    //connection.ChangeDatabase(database);

                    var indexes = GetIndexDefinitions(connection, schema, objectName);
                    foreach (var index in indexes)
                    {
                        scripts.AppendLine(GenerateIndexScript(index, schema, objectName, includeIfNotExists));
                        scripts.AppendLine();
                    }
                }
                finally
                {
                    if (connection.State == ConnectionState.Open)
                    {
                        connection.Close();
                    }
                }

                return scripts.ToString();
            }
        }

        private static IEnumerable<IndexDefinition> GetIndexDefinitions(SqlConnection connection, string schema, string tableName)
        {
            const string indexQuery = @"
SELECT 
    i.name AS IndexName,
    (
        SELECT STRING_AGG(CAST(c.name AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
        FROM sys.index_columns ic WITH (NOLOCK)
        JOIN sys.columns c WITH (NOLOCK) ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id 
        AND ic.index_id = i.index_id 
        AND ic.is_included_column = 0
    ) AS IndexColumns,
    (
        SELECT STRING_AGG(CAST(c.name AS NVARCHAR(MAX)), ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
        FROM sys.index_columns ic WITH (NOLOCK)
        JOIN sys.columns c WITH (NOLOCK) ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        WHERE ic.object_id = i.object_id 
        AND ic.index_id = i.index_id 
        AND ic.is_included_column = 1
    ) AS IncludedColumns,
    i.is_unique,
    i.filter_definition,
    i.type_desc,
    CASE 
        WHEN i.is_padded = 1 THEN 'PAD_INDEX = ON'
        ELSE 'PAD_INDEX = OFF'
    END + ', ' +
    CASE 
        WHEN s.no_recompute = 1 THEN 'STATISTICS_NORECOMPUTE = ON'
        ELSE 'STATISTICS_NORECOMPUTE = OFF'
    END + ', ' +
    'IGNORE_DUP_KEY = OFF, ONLINE = OFF' AS index_options
FROM sys.indexes i WITH (NOLOCK)
LEFT JOIN sys.stats s WITH (NOLOCK) ON i.object_id = s.object_id AND i.index_id = s.stats_id
WHERE OBJECT_SCHEMA_NAME(i.object_id) = @schema
    AND OBJECT_NAME(i.object_id) = @tableName
    AND i.type_desc = 'NONCLUSTERED'";

            using var command = new SqlCommand(indexQuery, connection);
            command.Parameters.AddWithValue("@schema", schema);
            command.Parameters.AddWithValue("@tableName", tableName);
            using var reader = command.ExecuteReader();
            var indexes = new List<IndexDefinition>();
            while (reader.Read())
            {
                indexes.Add(new IndexDefinition
                {
                    Name = reader["IndexName"].ToString(),
                    Columns = reader["IndexColumns"].ToString(),
                    IncludedColumns = reader["IncludedColumns"].ToString(),
                    IsUnique = (bool)reader["is_unique"],
                    FilterDefinition = reader["filter_definition"] as string,
                    IndexOptions = reader["index_options"].ToString()
                });
            }
            return indexes;
        }

        private static string GenerateIndexScript(IndexDefinition index, string schema, string objectName, bool includeIfNotExists)
        {
            var script = new StringBuilder();

            if (includeIfNotExists)
            {
                script.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index.Name}' AND object_id = OBJECT_ID(N'[{schema}].[{objectName}]'))");
                script.AppendLine("BEGIN");
            }

            script.Append($"CREATE ");
            if (index.IsUnique) script.Append("UNIQUE ");
            script.Append($"NONCLUSTERED INDEX [{index.Name}] ON [{schema}].[{objectName}] ");
            script.AppendLine($"([{index.Columns.Replace(", ", "], [")}])");

            if (!string.IsNullOrEmpty(index.IncludedColumns))
            {
                script.AppendLine($"INCLUDE ([{index.IncludedColumns.Replace(", ", "], [")}])");
            }

            if (!string.IsNullOrEmpty(index.FilterDefinition))
            {
                script.AppendLine($"WHERE {index.FilterDefinition}");
            }

            script.AppendLine($"WITH ({index.IndexOptions})");

            if (includeIfNotExists)
            {
                script.AppendLine("END");
            }

            return script.ToString();
        }

        private class IndexDefinition
        {
            public string Name { get; set; }
            public string Columns { get; set; }
            public string IncludedColumns { get; set; }
            public bool IsUnique { get; set; }
            public string FilterDefinition { get; set; }
            public string IndexOptions { get; set; }
        }


        internal static string GetDropIndexStatements(Table table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table), "Table object cannot be null");
            }

            // More defensive check - validate that Indexes collection is not null
            if (table.Indexes == null)
            {
                return string.Empty;
            }

            var nonClusteredIndexes = new List<Index>();

            // Safer iteration to handle potentially corrupted index objects
            foreach (Index index in table.Indexes)
            {
                try
                {
                    // Skip null indexes
                    if (index == null)
                        continue;

                    // Try accessing IndexType in a try-catch to handle corrupted objects
                    var indexType = index.IndexType;
                    if (indexType != IndexType.ClusteredIndex)
                    {
                        nonClusteredIndexes.Add(index);
                    }
                }
                catch (Exception)
                {
                    // Optionally log the error
                    // Logger.LogWarning($"Skipping index due to error: {ex.Message}");
                    continue;
                }
            }

            if (!nonClusteredIndexes.Any())
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine("    BEGIN TRANSACTION;");

            foreach (var index in nonClusteredIndexes)
            {
                // Additional null checks for index properties
                var indexName = string.IsNullOrEmpty(index.Name) ? "UnknownIndex" : index.Name;
                var schemaName = string.IsNullOrEmpty(table.Schema) ? "dbo" : table.Schema;
                var tableName = string.IsNullOrEmpty(table.Name) ? "UnknownTable" : table.Name;

                sb.AppendLine($"    DROP INDEX [{indexName}] ON [{schemaName}].[{tableName}];");
            }

            sb.AppendLine("    COMMIT TRANSACTION;");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine("    IF @@TRANCOUNT > 0");
            sb.AppendLine("        ROLLBACK TRANSACTION;");
            sb.AppendLine("    THROW;");
            sb.AppendLine("END CATCH");

            return sb.ToString();
        }


        internal static string DropObjectIndexes(string conString, string Database, string Schema, string Object)
        {
            SqlConnection sqlCon = new SqlConnection(conString);
            ServerConnection srvCon = new ServerConnection(sqlCon);
            Server srv = new Server(srvCon);

            StringBuilder rValue = new StringBuilder();
            try
            {
                Database db = srv.Databases[Database];

                Table tblObj = null;
                try
                {
                    string serverName = srv.Urn.Value.Split('/')[0].Replace("Server[@Name='", "").Replace("']", "");
                    var tblUrn = SmoHelper.CreateTableUrnFromComponents(serverName, Database, Schema, Object);
                    var tbl = srv.GetSmoObject(tblUrn);
                    tblObj = tbl as Table;
                    
                } catch {}

                if (tblObj != null)
                {
                    var dropIndexStatment = GetDropIndexStatements(tblObj);
                    CommonDB.ExecDDLScript(sqlCon, dropIndexStatment, 360, false);
                }
                
            }
            catch
            {

                throw;
            }
            finally
            {
                srv.ConnectionContext.Disconnect();
                srvCon.Disconnect();
                sqlCon.Close();
                sqlCon.Dispose();
            }

            return rValue.ToString();
        }
    }
}