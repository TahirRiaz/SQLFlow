using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.SqlClient;
using Index = Microsoft.SqlServer.Management.Smo.Index;

namespace SQLFlowCore.Services.TsqlParser
{
    internal class IndexManagement
    {
        public string EnsureIndexes(string connectionString, string indexScript)
        {
            string rvalue = "";
            using (var connection = new SqlConnection(connectionString))
            {
                var server = new Server(new ServerConnection(connection));
                var database = server.Databases[connection.Database];

                rvalue = SyncIndexes(database, indexScript);
            }

            return rvalue;
        }

        public string EnsureIndexes(Database database, string indexScript)
        {
            string rvalue = "";
            rvalue = SyncIndexes(database, indexScript);

            return rvalue;
        }

        private string SyncIndexes(Database database, string indexScript)
        {
            StringBuilder logMessage = new StringBuilder();
            var parser = ParserForSql.GetParser(indexScript);
            IList<ParseError> errors;
            var fragment = parser.Parse(new StringReader(indexScript), out errors);
            if (errors.Count > 0)
            {
                logMessage.Append($"Errors in SQL script:{Environment.NewLine}");
                foreach (var error in errors)
                {
                    logMessage.Append(error.Message);
                }

                return logMessage.ToString();
            }

            var visitor = new CreateIndexVisitor();
            fragment.Accept(visitor);

            foreach (var desiredIndex in visitor.IndexDefinitions)
            {
                if (desiredIndex.OnName == null || string.IsNullOrWhiteSpace(desiredIndex.OnName.BaseIdentifier?.Value))
                {
                    logMessage.Append($"Invalid table name in index definition.{Environment.NewLine}");
                    continue;
                }

                var table = database.Tables[desiredIndex.OnName.BaseIdentifier.Value,
                    desiredIndex.OnName.SchemaIdentifier?.Value];
                if (table == null)
                {
                    logMessage.Append($"Table {desiredIndex.OnName.BaseIdentifier.Value} not found.{Environment.NewLine}");
                    continue;
                }

                // Additional null check for index name before attempting to access it
                if (desiredIndex.Name == null || string.IsNullOrWhiteSpace(desiredIndex.Name.Value))
                {
                    logMessage.Append($"Table {desiredIndex.OnName.BaseIdentifier.Value} not found.{Environment.NewLine}");
                    continue;
                }

                // Check if the table has any indexes at all before attempting to access them
                Index existingIndex = null;
                if (table.Indexes != null && table.Indexes.Contains(desiredIndex.Name.Value))
                {
                    existingIndex = table.Indexes[desiredIndex.Name.Value];
                }

                if (existingIndex != null && !IsIndexDefinitionSame(existingIndex, desiredIndex))
                {
                    existingIndex.Drop();
                    existingIndex = null;
                }

                if (existingIndex == null)
                {
                    CreateIndex(logMessage, desiredIndex, table);
                }
            }

            return logMessage.ToString();
        }

        private bool IsIndexDefinitionSame(Index existingIndex,
            CreateIndexStatement desiredIndex)
        {
            // Get all included columns
            var includedColumns = existingIndex.IndexedColumns.Cast<IndexedColumn>()
                .Where(col => col.IsIncluded)
                .ToList();

            var indexColumns = existingIndex.IndexedColumns.Cast<IndexedColumn>()
                .Where(col => !col.IsIncluded)
                .ToList();

            // Check if Index contains the same amount of columns
            if (indexColumns.Count != desiredIndex.Columns.Count)
                return false;

            // Check if Index contains the same amount of included columns
            if (includedColumns.Count != desiredIndex.IncludeColumns.Count)
                return false;

            // Compare column names (ignore case)
            for (int i = 0; i < indexColumns.Count; i++)
            {
                if (!indexColumns[i].Name.Equals(desiredIndex.Columns[i].Column.MultiPartIdentifier.Identifiers[0].Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Compare included column names (ignore case)
            for (int i = 0; i < includedColumns.Count; i++)
            {
                if (!includedColumns[i].Name.Equals(desiredIndex.IncludeColumns[i].MultiPartIdentifier.Identifiers[0].Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }


        private void CreateIndex(StringBuilder sb, CreateIndexStatement desiredIndex, Table table)
        {
            var newIndex = new Index(table, desiredIndex.Name.Value)
            {
                IndexKeyType = IndexKeyType.None,
                IsUnique = false,
                IsClustered = false
            };

            foreach (var column in desiredIndex.Columns)
            {
                var columnName = column.Column.MultiPartIdentifier.Identifiers[0].Value;
                newIndex.IndexedColumns.Add(new IndexedColumn(newIndex, columnName));
            }

            foreach (var includedColumn in desiredIndex.IncludeColumns)
            {
                var columnName = includedColumn.MultiPartIdentifier.Identifiers[0].Value;
                newIndex.IndexedColumns.Add(new IndexedColumn(newIndex, columnName) { IsIncluded = true });
            }

            newIndex.Create();
            sb.Append($"Created index {newIndex.Name} on table {table.Name}{Environment.NewLine}");
        }
    }


    class CreateIndexVisitor : TSqlFragmentVisitor
    {
        public List<CreateIndexStatement> IndexDefinitions { get; } = new();

        public override void Visit(CreateIndexStatement node)
        {
            IndexDefinitions.Add(node);
        }
    }

}


