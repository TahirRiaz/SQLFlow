using System;
using System.Collections.Generic;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.IO;

/// <summary>
/// Represents a utility class for handling temporary tables in T-SQL scripts.
/// </summary>
/// <remarks>
/// This class provides methods to find, generate create table statements, and generate drop table statements for temporary tables.
/// </remarks>
namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Provides functionality for handling temporary tables in T-SQL scripts.
    /// </summary>
    /// <remarks>
    /// The TempTables class includes methods to find temporary tables in a T-SQL script, 
    /// generate CREATE TABLE statements for temporary tables, and generate DROP TABLE statements for temporary tables.
    /// </remarks>
    internal class TempTables
    {
        /// <summary>
        /// Finds the names of all temporary tables in a given T-SQL script.
        /// </summary>
        /// <param name="tSqlScript">The T-SQL script to search for temporary tables.</param>
        /// <returns>A list of unique temporary table names found in the T-SQL script.</returns>
        /// <exception cref="InvalidOperationException">Thrown when parse errors occur while parsing the T-SQL script.</exception>
        /// <remarks>
        /// This method parses the provided T-SQL script using the TSql150Parser. It then uses a TempTableVisitor to visit each node in the parse tree and add the names of any temporary tables to a HashSet. This HashSet is then converted to a List and returned.
        /// </remarks>
        internal static List<string> Find(string tSqlScript)
        {
            TSql150Parser parser = new TSql150Parser(false);
            IList<ParseError> parseErrors;
            TSqlFragment fragment = parser.Parse(new StringReader(tSqlScript), out parseErrors);

            if (parseErrors.Count > 0)
            {
                throw new InvalidOperationException("Parse errors occurred.");
            }

            TempTableVisitor visitor = new TempTableVisitor();
            fragment.Accept(visitor);

            HashSet<string> myHashSet = new HashSet<string>(visitor.TempTables);

            List<string> uniqueList = new List<string>(myHashSet);

            return uniqueList;
        }

        /// <summary>
        /// Generates the CREATE TABLE statements for a list of temporary table names.
        /// </summary>
        /// <param name="tempTableNames">The list of temporary table names.</param>
        /// <returns>A list of CREATE TABLE statements for the temporary tables.</returns>
        /// <remarks>
        /// This method iterates over the provided list of temporary table names and generates a CREATE TABLE statement for each one. The generated statements have a single column named 'Column1' of type INT.
        /// </remarks>
        internal static List<string> GenerateCreateTableStatements(List<string> tempTableNames)
        {
            List<string> createTableStatements = new List<string>();

            foreach (var tableName in tempTableNames)
            {


                string createStatement = $"CREATE TABLE {tableName} (Column1 INT);";
                createTableStatements.Add(createStatement);
            }

            return createTableStatements;
        }

        /// <summary>
        /// Generates the DROP TABLE statements for a list of temporary table names.
        /// </summary>
        /// <param name="tempTableNames">The list of temporary table names.</param>
        /// <returns>A list of DROP TABLE statements for the temporary tables.</returns>
        /// <remarks>
        /// This method iterates over the provided list of temporary table names and generates a DROP TABLE statement for each one.
        /// </remarks>
        internal static List<string> GenerateDropTableStatements(List<string> tempTableNames)
        {
            List<string> dropTableStatements = new List<string>();

            foreach (var tableName in tempTableNames)
            {
                string dropStatement = $"DROP TABLE {tableName};";
                dropTableStatements.Add(dropStatement);
            }

            return dropTableStatements;
        }
    }

    /// <summary>
    /// Represents a visitor that identifies temporary tables in a T-SQL script.
    /// </summary>
    /// <remarks>
    /// The TempTableVisitor class extends the TSqlFragmentVisitor class and overrides the Visit method for NamedTableReference nodes. 
    /// It adds the names of temporary tables (those starting with '#') to the TempTables list.
    /// </remarks>
    internal class TempTableVisitor : TSqlFragmentVisitor
    {
        internal List<string> TempTables { get; } = new();

        /// <summary>
        /// Visits the NamedTableReference nodes in the T-SQL script parse tree.
        /// </summary>
        /// <param name="node">The NamedTableReference node to visit.</param>
        /// <remarks>
        /// This method checks if the base identifier of the schema object in the node starts with '#', 
        /// indicating a temporary table. If it does, the method adds the base identifier value to the TempTables list.
        /// </remarks>
        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject.BaseIdentifier.Value.StartsWith("#"))
            {
                TempTables.Add(node.SchemaObject.BaseIdentifier.Value);
            }
        }
    }



}
