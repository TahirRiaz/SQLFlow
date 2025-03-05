using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Provides extension methods for the HashSet&lt;T&gt; class.
    /// </summary>
    /// <remarks>
    /// This class is used to add additional functionality to the HashSet&lt;T&gt; class.
    /// Currently, it includes a method to convert a HashSet to a List.
    /// </remarks>
    internal static class HashSetExtensions
    {
        internal static List<T> ToList<T>(this HashSet<T> hashSet)
        {
            return new List<T>(hashSet);
        }
    }

    /// <summary>
    /// The `DependencyExtractor` class is a visitor that traverses a T-SQL script and extracts information about the dependencies in the script.
    /// </summary>
    /// <remarks>
    /// This class is derived from the `TSqlFragmentVisitor` class provided by the `Microsoft.SqlServer.TransactSql.ScriptDom` namespace. 
    /// It overrides several visit methods to extract information about the different types of SQL statements in the script.
    /// The extracted information includes the tables that are selected, updated, deleted, created, dropped, and inserted into, 
    /// as well as the views that are created, the stored procedures that are referenced, and the common table expressions that are used.
    /// </remarks>
    internal class DependencyExtractor : TSqlFragmentVisitor
    {
        private string DefaultDatabase { get; }
        private string DefaultSchema { get; }
        private string BaseObjectName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DependencyExtractor"/> class.
        /// </summary>
        /// <param name="defaultDatabase">The default database to be used in the extraction process.</param>
        /// <param name="defaultSchema">The default schema to be used in the extraction process.</param>
        /// <param name="baseObjectName">The base object name to be used in the extraction process.</param>
        internal DependencyExtractor(string defaultDatabase, string defaultSchema, string baseObjectName)
        {
            DefaultDatabase = defaultDatabase;
            DefaultSchema = defaultSchema;
            BaseObjectName = baseObjectName;
        }

        /// <summary>
        /// Gets the fully qualified name of the given schema object.
        /// </summary>
        /// <param name="schemaObject">The schema object for which to get the fully qualified name.</param>
        /// <returns>
        /// The fully qualified name of the schema object. If the schema object is a temporary table, 
        /// the base identifier of the schema object is returned. Otherwise, the fully qualified name 
        /// is constructed using the database identifier, schema identifier, and base identifier of the schema object.
        /// </returns>
        private string GetFullyQualifiedName(SchemaObjectName schemaObject)
        {
            bool isTempTable = schemaObject.BaseIdentifier.Value.Contains("#");
            if (isTempTable)
            {
                return $"[{schemaObject.BaseIdentifier?.Value}]";
            }
            else
            {
                string database = schemaObject.DatabaseIdentifier?.Value ?? DefaultDatabase;
                string schema = schemaObject.SchemaIdentifier?.Value ?? DefaultSchema;
                string baseIdentifier = schemaObject.BaseIdentifier?.Value ?? "Unknown";

                return $"[{database}].[{schema}].[{baseIdentifier}]";
            }
        }

        internal HashSet<string> SelectTables { get; } = new();
        internal HashSet<string> UpdateTables { get; } = new();
        internal HashSet<string> DeleteTables { get; } = new();
        internal HashSet<string> CreateTables { get; } = new();
        internal HashSet<string> DropTables { get; } = new();
        internal HashSet<string> InsertTables { get; } = new();
        internal HashSet<string> CreateViews { get; } = new();
        internal HashSet<string> StoredProcedures { get; } = new();
        internal HashSet<string> CTETables { get; } = new();
        internal HashSet<string> CTEQualfiedName { get; } = new();

        private Dictionary<string, string> tableAliases = new();

        /// <summary>
        /// Gets a collection of dependency objects that are processed before the current object.
        /// This collection includes all tables that are used in SELECT, UPDATE, DELETE, CREATE, and DROP operations,
        /// as well as views created. Temporary tables and tables that contain the base object name are excluded.
        /// The collection also excludes tables used in INSERT operations and tables with a qualified name used in Common Table Expressions (CTEs).
        /// </summary>
        internal HashSet<string> BeforeDependencyObjects
        {
            get
            {
                var allTables = new HashSet<string>();
                allTables.UnionWith(SelectTables);
                allTables.UnionWith(UpdateTables);
                allTables.UnionWith(DeleteTables);
                allTables.UnionWith(CreateTables);
                allTables.UnionWith(DropTables);
                allTables.UnionWith(CreateViews);
                allTables.RemoveWhere(item => item.Contains("#")); // Remove temp tables
                allTables.RemoveWhere(item => item.Contains(BaseObjectName, StringComparison.InvariantCultureIgnoreCase)); // Remove temp tables
                allTables.RemoveWhere(item => IsSystemObject(item)); // Remove system objects
                allTables.ExceptWith(InsertTables);
                allTables.ExceptWith(CTEQualfiedName);
                return allTables;
                //allTables.UnionWith(StoredProcedures);
                //allTables.UnionWith(CTETables);
            }
        }

        /// <summary>
        /// Gets a collection of dependency objects that are processed after the current object.
        /// </summary>
        /// <remarks>
        /// This property returns a set of all tables involved in insert operations, excluding temporary tables and tables that contain the base object name.
        /// </remarks>
        /// <returns>
        /// A HashSet of strings representing the names of the dependency objects processed after the current object.
        /// </returns>
        internal HashSet<string> AfterDependencyObjects
        {
            get
            {
                var allTables = new HashSet<string>();
                allTables.UnionWith(InsertTables);
                allTables.RemoveWhere(item => item.Contains("#")); // Remove temp tables
                allTables.RemoveWhere(item => item.Contains(BaseObjectName, StringComparison.InvariantCultureIgnoreCase)); // Remove temp tables
                allTables.RemoveWhere(item => IsSystemObject(item)); // Remove system objects
                return allTables;
                //allTables.UnionWith(CTETables);
            }
        }


        public override void ExplicitVisit(QuerySpecification node)
        {
            if (node.FromClause != null)
            {
                foreach (var tableSource in node.FromClause.TableReferences)
                {
                    tableSource.Accept(this);
                }
            }
        }


        public override void ExplicitVisit(SelectStatement node)
        {
            // This method already exists, but ensure it properly handles CTEs
            if (node.WithCtesAndXmlNamespaces != null)
            {
                node.WithCtesAndXmlNamespaces.Accept(this);
            }

            if (node.QueryExpression != null)
            {
                node.QueryExpression.Accept(this);
            }

            base.ExplicitVisit(node);
        }

        public override void Visit(WithCtesAndXmlNamespaces withCtesAndXmlNamespaces)
        {
            foreach (var cte in withCtesAndXmlNamespaces.CommonTableExpressions)
            {
                cte.Accept(this);
            }
            base.Visit(withCtesAndXmlNamespaces);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            if (node.Alias != null)
            {
                var tableName = GetFullyQualifiedName(node.SchemaObject);
                tableAliases[node.Alias.Value] = tableName;
            }
            
            SelectTables.Add(GetFullyQualifiedName(node.SchemaObject));
        }

        public override void ExplicitVisit(QualifiedJoin node)
        {
            node.FirstTableReference.Accept(this);
            node.SecondTableReference.Accept(this);
        }

        public override void ExplicitVisit(UnqualifiedJoin node)
        {
            node.FirstTableReference.Accept(this);
            node.SecondTableReference.Accept(this);
        }

        private Dictionary<string, HashSet<string>> cteDependencies = new Dictionary<string, HashSet<string>>();

        public override void ExplicitVisit(CommonTableExpression node)
        {
            string cteName = node.ExpressionName.Value;
            CTETables.Add(cteName);

            string fullName = $"[{DefaultDatabase}].[{DefaultSchema}].[{cteName}]";
            CTEQualfiedName.Add(fullName);

            // Create a set to track what this CTE depends on
            HashSet<string> dependencies = new HashSet<string>();
            cteDependencies[cteName] = dependencies;

            // Keep track of the current size of select tables to detect new additions
            int initialSelectCount = SelectTables.Count;

            // Visit the query expression
            if (node.QueryExpression != null)
            {
                // Add explicit debug to see what type the query expression is
                Console.WriteLine($"CTE Query Expression Type: {node.QueryExpression.GetType().Name}");
                node.QueryExpression.Accept(this);
            }

            // Any tables added to SelectTables during the query expression visit
            // are dependencies of this CTE
            foreach (var table in SelectTables.Skip(initialSelectCount))
            {
                dependencies.Add(table);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            node.QueryExpression.Accept(this);
        }

        // Update your UPDATE statement visitor
        public override void ExplicitVisit(UpdateStatement node)
        {
            Dictionary<string, string> updateRef = new Dictionary<string, string>();

            // Handle FROM clause
            if (node.UpdateSpecification?.FromClause?.TableReferences != null)
            {
                foreach (var tableSource in node.UpdateSpecification.FromClause.TableReferences)
                {
                    ProcessTableSource(tableSource, updateRef);
                    tableSource.Accept(this); // Make sure to visit all table references
                }
            }

            // Handle target
            if (node.UpdateSpecification?.Target != null)
            {
                if (node.UpdateSpecification.Target is NamedTableReference namedTableTarget)
                {
                    UpdateTables.Add(GetFullyQualifiedName(namedTableTarget.SchemaObject));
                }
                else
                {
                    // Could be a derived table or other reference
                    node.UpdateSpecification.Target.Accept(this);
                }
            }

            // Visit the WHERE clause for any subqueries
            if (node.UpdateSpecification?.WhereClause != null)
            {
                node.UpdateSpecification.WhereClause.Accept(this);
            }

            base.ExplicitVisit(node);
        }

        /// <summary>
        /// Processes a table reference to extract its name and alias information.
        /// Recursively processes joined tables and other complex table structures.
        /// </summary>
        /// <param name="tableSource">The table reference to process</param>
        /// <param name="referenceMap">Dictionary to populate with alias-to-table mappings</param>
        /// <param name="collectSelectTables">Whether to add found tables to SelectTables collection</param>
        private void ProcessTableSource(TableReference tableSource, Dictionary<string, string> referenceMap, bool collectSelectTables = true)
        {
            if (tableSource == null)
                return;

            switch (tableSource)
            {
                case NamedTableReference namedTable:
                    var tableName = GetFullyQualifiedName(namedTable.SchemaObject);

                    // Add to the method-level reference map
                    if (namedTable.Alias != null)
                    {
                        referenceMap[namedTable.Alias.Value] = tableName;
                        // Also update the class-level tableAliases for consistent alias resolution
                        tableAliases[namedTable.Alias.Value] = tableName;
                    }

                    // Add to select tables if requested
                    if (collectSelectTables)
                    {
                        SelectTables.Add(tableName);
                    }
                    break;

                case QualifiedJoin qualifiedJoin:
                    // Process both sides of the join
                    ProcessTableSource(qualifiedJoin.FirstTableReference, referenceMap, collectSelectTables);
                    ProcessTableSource(qualifiedJoin.SecondTableReference, referenceMap, collectSelectTables);

                    // Process ON clause for subqueries
                    if (qualifiedJoin.SearchCondition != null)
                    {
                        qualifiedJoin.SearchCondition.Accept(this);
                    }
                    break;

                case UnqualifiedJoin unqualifiedJoin:
                    ProcessTableSource(unqualifiedJoin.FirstTableReference, referenceMap, collectSelectTables);
                    ProcessTableSource(unqualifiedJoin.SecondTableReference, referenceMap, collectSelectTables);
                    break;

                case PivotedTableReference pivotedTable:
                    ProcessTableSource(pivotedTable.TableReference, referenceMap, collectSelectTables);
                    break;

                case QueryDerivedTable queryDerivedTable:
                    // For derived tables, we need to visit the query expression
                    if (queryDerivedTable.QueryExpression != null)
                    {
                        queryDerivedTable.QueryExpression.Accept(this);
                    }

                    // Track the alias if present
                    if (queryDerivedTable.Alias != null)
                    {
                        string derivedTableName = $"#DerivedTable_{queryDerivedTable.Alias.Value}";
                        referenceMap[queryDerivedTable.Alias.Value] = derivedTableName;
                        tableAliases[queryDerivedTable.Alias.Value] = derivedTableName;
                    }
                    break;

                case VariableTableReference variableTable:
                    // For table variables, we need to find the right property to access the variable name
                    // Try multiple approaches to get the variable name
                    string varName = null;

                    // Attempt to use reflection to find the property that contains the variable name
                    try
                    {
                        // Get all properties of the VariableTableReference type
                        var properties = variableTable.GetType().GetProperties();

                        // Look for properties that might contain the variable name
                        foreach (var prop in properties)
                        {
                            // Try to find properties that sound like they would contain a name
                            if (prop.Name.Contains("Name") ||
                                prop.Name.Contains("Variable") ||
                                prop.Name.Contains("Reference") ||
                                prop.Name == "Value")
                            {

                                var value = prop.GetValue(variableTable);
                                if (value != null)
                                {
                                    // If the property value is a string, use it directly
                                    if (value is string strValue)
                                    {
                                        varName = strValue;
                                        break;
                                    }

                                    // If the property value is an object, try to look for a Value property on it
                                    var valueProperty = value.GetType().GetProperty("Value");
                                    if (valueProperty != null)
                                    {
                                        var nameValue = valueProperty.GetValue(value);
                                        if (nameValue is string strNameValue)
                                        {
                                            varName = strNameValue;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        // If we still don't have a name, try ToString() as a last resort
                        if (string.IsNullOrEmpty(varName))
                        {
                            varName = variableTable.ToString();
                        }
                    }
                    catch
                    {
                        // If reflection fails, use a default name pattern
                        varName = $"@UnknownVar_{Guid.NewGuid().ToString().Substring(0, 8)}";
                    }

                    // Ensure the variable name has the @ prefix
                    if (!string.IsNullOrEmpty(varName))
                    {
                        string tableVarName = varName.StartsWith("@") ? varName : $"@{varName}";
                        referenceMap[varName] = tableVarName;
                        tableAliases[varName] = tableVarName;

                        if (collectSelectTables)
                        {
                            SelectTables.Add(tableVarName);
                        }
                    }
                    break;

                case ChangeTableChangesTableReference changeTableChanges:
                    // CHANGETABLE(CHANGES ...) function
                    if (changeTableChanges.Target != null)
                    {
                        string changeTableName = GetFullyQualifiedName(changeTableChanges.Target);
                        if (collectSelectTables)
                        {
                            SelectTables.Add(changeTableName);
                        }
                    }
                    break;

                case ChangeTableVersionTableReference changeTableVersion:
                    // CHANGETABLE(VERSION ...) function
                    if (changeTableVersion.Target != null)
                    {
                        string versionTableName = GetFullyQualifiedName(changeTableVersion.Target);
                        if (collectSelectTables)
                        {
                            SelectTables.Add(versionTableName);
                        }
                    }
                    break;

                case SchemaObjectFunctionTableReference functionTableRef:
                    // Table-valued functions
                    if (functionTableRef.SchemaObject != null)
                    {
                        string functionName = GetFullyQualifiedName(functionTableRef.SchemaObject);

                        if (functionTableRef.Alias != null)
                        {
                            referenceMap[functionTableRef.Alias.Value] = functionName;
                            tableAliases[functionTableRef.Alias.Value] = functionName;
                        }

                        if (collectSelectTables)
                        {
                            SelectTables.Add(functionName);
                        }
                    }
                    break;

                case JoinParenthesisTableReference joinParenthesis:
                    // Handle joins in parentheses
                    if (joinParenthesis.Join != null)
                    {
                        ProcessTableSource(joinParenthesis.Join, referenceMap, collectSelectTables);
                    }
                    break;

                case BuiltInFunctionTableReference builtInFunction:
                    // Functions like OPENQUERY, OPENROWSET, etc.
                    if (builtInFunction.Alias != null)
                    {
                        string builtInName = $"#{builtInFunction.Name}_{builtInFunction.Alias.Value}";
                        referenceMap[builtInFunction.Alias.Value] = builtInName;
                        tableAliases[builtInFunction.Alias.Value] = builtInName;
                    }
                    break;


                case FullTextTableReference fullTextTable:
                    // CONTAINSTABLE or FREETEXTTABLE
                    if (fullTextTable.TableName != null)
                    {
                        string fullTextTableName = GetFullyQualifiedName(fullTextTable.TableName);

                        if (fullTextTable.Alias != null)
                        {
                            referenceMap[fullTextTable.Alias.Value] = fullTextTableName;
                            tableAliases[fullTextTable.Alias.Value] = fullTextTableName;
                        }

                        if (collectSelectTables)
                        {
                            SelectTables.Add(fullTextTableName);
                        }
                    }
                    break;

                // Additional cases for other table reference types as needed

                default:
                    // For any other types, try to use reflection to get table references
                    try
                    {
                        var tableRefProperties = tableSource.GetType().GetProperties()
                            .Where(p => typeof(TableReference).IsAssignableFrom(p.PropertyType));

                        foreach (var prop in tableRefProperties)
                        {
                            var childTableRef = prop.GetValue(tableSource) as TableReference;
                            if (childTableRef != null)
                            {
                                ProcessTableSource(childTableRef, referenceMap, collectSelectTables);
                            }
                        }

                        // Also look for collections of table references
                        var tableRefCollections = tableSource.GetType().GetProperties()
                            .Where(p => typeof(IEnumerable<TableReference>).IsAssignableFrom(p.PropertyType));

                        foreach (var prop in tableRefCollections)
                        {
                            var collection = prop.GetValue(tableSource) as IEnumerable<TableReference>;
                            if (collection != null)
                            {
                                foreach (var item in collection)
                                {
                                    ProcessTableSource(item, referenceMap, collectSelectTables);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fallback if reflection fails - just accept the node directly
                        tableSource.Accept(this);
                    }
                    break;
            }
        }


        public override void ExplicitVisit(DeleteStatement node)
        {
            Dictionary<string, string> delRef = new Dictionary<string, string>();
            // Capture alias-to-table mapping within this specific DELETE statement
            if (node.DeleteSpecification.FromClause != null)
            {
                foreach (var tableSource in node.DeleteSpecification.FromClause.TableReferences)
                {
                    if (tableSource is NamedTableReference namedTable)
                    {
                        var tableName = GetFullyQualifiedName(namedTable.SchemaObject);
                        if (namedTable.Alias != null)
                        {
                            delRef[namedTable.Alias.Value] = tableName;
                        }
                    }
                }
            }

            // Now handle the target of the DELETE statement
            if (node.DeleteSpecification.Target is NamedTableReference namedTableTarget)
            {
                var aliasOrTableName = GetFullyQualifiedName(namedTableTarget.SchemaObject);
                if (delRef.TryGetValue(namedTableTarget.SchemaObject.BaseIdentifier.Value, out var actualTableName))
                {
                    DeleteTables.Add(actualTableName);
                }
                else
                {
                    DeleteTables.Add(aliasOrTableName);
                }
            }

        }


        public override void ExplicitVisit(CreateTableStatement node)
        {
            CreateTables.Add(GetFullyQualifiedName(node.SchemaObjectName));
        }


        public override void ExplicitVisit(DropTableStatement node)
        {
            foreach (var table in node.Objects)
            {
                DropTables.Add(GetFullyQualifiedName(table));
            }
        }


        public override void ExplicitVisit(InsertStatement node)
        {
            if (node.InsertSpecification.Target is NamedTableReference namedTable)
            {
                InsertTables.Add(GetFullyQualifiedName(namedTable.SchemaObject));
            }

            // Handle the source query - this is important for INSERT ... SELECT...
            if (node.InsertSpecification.InsertSource != null)
            {
                Console.WriteLine($"Insert Source Type: {node.InsertSpecification.InsertSource.GetType().Name}");
                node.InsertSpecification.InsertSource.Accept(this);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            CreateViews.Add(GetFullyQualifiedName(node.SchemaObjectName));
            // Analyze the query expression associated with the view

            // If the QueryExpression in the view is a more complex type, visit it.
            if (node.SelectStatement.QueryExpression != null)
            {
                node.SelectStatement.QueryExpression.Accept(this);
            }
            else // otherwise, it's a simple select statement
            {
                node.SelectStatement.Accept(this);
            }
        }


        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference != null)
            {
                // Case 1: Procedure is identified by a direct identifier
                if (node.ProcedureReference.ProcedureReference != null)
                {
                    string procName = node.ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value;
                    StoredProcedures.Add(GetFullyQualifiedName(node.ProcedureReference.ProcedureReference.Name));
                }
                // Case 2: Procedure is identified by a variable
                else if (node.ProcedureReference.ProcedureVariable != null)
                {
                    string variableName = node.ProcedureReference.ProcedureVariable.Name;
                    StoredProcedures.Add(variableName);
                }
                else
                {
                    // Case 3: ProcedureReference exists but neither Identifier nor Variable is set
                    // Log this case as it's unusual and not expected in typical SQL scripts
                    // You could add some logging code here
                    StoredProcedures.Add("Unidentified Procedure Reference");
                }
            }
            else
            {
                // Case 4: ProcedureReference itself is null, which is highly unusual and likely an error
                // Log this case as it would indicate a problem in the SQL script or the parsing thereof
                // You could add some logging code here
                StoredProcedures.Add("Null Procedure Reference");
            }
        }


        public override void ExplicitVisit(BinaryQueryExpression node)
        {
            // Visit both the left and right sides of the binary expression (UNION, EXCEPT, INTERSECT)
            node.FirstQueryExpression.Accept(this);
            node.SecondQueryExpression.Accept(this);
        }

        public override void ExplicitVisit(AssignmentSetClause node)
        {
            // In the updated Microsoft.SqlServer.TransactSql.ScriptDom, the property might be named differently
            // Common alternatives could be "NewValue", "Expression", or "AssignmentValue"
            if (node.NewValue != null) // Try using NewValue instead of Value
            {
                node.NewValue.Accept(this);
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ScalarSubquery node)
        {
            // Process the SELECT within the scalar subquery
            if (node.QueryExpression != null)
            {
                node.QueryExpression.Accept(this);
            }
            base.ExplicitVisit(node);
        }


        public override void ExplicitVisit(MergeStatement node)
        {
            // The target table receives updates/inserts/deletes
            if (node.MergeSpecification.Target is NamedTableReference targetTable)
            {
                string targetName = GetFullyQualifiedName(targetTable.SchemaObject);
                UpdateTables.Add(targetName);
                InsertTables.Add(targetName);
                DeleteTables.Add(targetName);
            }

            // The source table is read from
            if (node.MergeSpecification.TableReference is NamedTableReference sourceTable)
            {
                SelectTables.Add(GetFullyQualifiedName(sourceTable.SchemaObject));
            }
            else if (node.MergeSpecification.TableReference != null)
            {
                // Handle other table reference types (derived tables, etc.)
                node.MergeSpecification.TableReference.Accept(this);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(PivotedTableReference node)
        {
            // Visit the underlying table
            if (node.TableReference != null)
            {
                node.TableReference.Accept(this);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            // Truncate is similar to delete, so add to delete tables
            DeleteTables.Add(GetFullyQualifiedName(node.TableName));

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TableReferenceWithAlias node)
        {
            // Instead of directly casting to specific types that might not exist,
            // check the type name or specific properties that would indicate the kind of join

            // Get the type name to identify what kind of table reference it is
            string typeName = node.GetType().Name;

            // Handle the table reference based on available properties
            if (typeName.Contains("Apply"))
            {
                // For any type of apply (cross apply, outer apply)
                // Use reflection to access properties safely
                var firstTableProperty = node.GetType().GetProperty("TableReference");
                var secondTableProperty = node.GetType().GetProperty("TableReference2") ??
                                          node.GetType().GetProperty("OuterReference") ??
                                          node.GetType().GetProperty("RightTableReference");

                if (firstTableProperty != null)
                {
                    var firstTable = firstTableProperty.GetValue(node) as TableReference;
                    if (firstTable != null)
                        firstTable.Accept(this);
                }

                if (secondTableProperty != null)
                {
                    var secondTable = secondTableProperty.GetValue(node) as TableReference;
                    if (secondTable != null)
                        secondTable.Accept(this);
                }
            }
            

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(StatementList node)
        {
            // Visit all statements in the batch
            if (node.Statements != null)
            {
                foreach (var statement in node.Statements)
                {
                    statement.Accept(this);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableStatement node)
        {
            // Capture the table being altered
            UpdateTables.Add(GetFullyQualifiedName(node.SchemaObjectName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExistsPredicate node)
        {
            if (node.Subquery != null)
            {
                node.Subquery.Accept(this);
            }
            base.ExplicitVisit(node);
        }

        private bool IsSystemObject(string fullyQualifiedName)
        {
            // Parse the fully qualified name
            string[] parts = fullyQualifiedName.Replace("[", "").Replace("]", "").Split('.');

            if (parts.Length < 3)
                return false;

            string dbName = parts[0];
            string schemaName = parts[1];
            string objectName = parts[2];

            // 1. System databases
            if (new[] { "master", "model", "msdb", "tempdb", "resource" }
                .Contains(dbName, StringComparer.OrdinalIgnoreCase))
                return true;

            // 2. System schemas
            if (new[] { "sys", "INFORMATION_SCHEMA", "db_owner", "db_accessadmin",
                    "db_securityadmin", "db_ddladmin", "db_backupoperator",
                    "db_datareader", "db_datawriter", "db_denydatareader",
                    "db_denydatawriter" }
                .Contains(schemaName, StringComparer.OrdinalIgnoreCase))
                return true;

            // 3. System stored procedures pattern
            if (objectName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
                objectName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
                return true;

            // 4. Other common system objects
            if (objectName.StartsWith("sys", StringComparison.OrdinalIgnoreCase) &&
                !objectName.Equals("sysutility_ucp_core", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

    }
}
