using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Represents a parser that extracts dependencies from SQL code.
    /// </summary>
    /// <remarks>
    /// This class is used to parse SQL code and extract information about the dependencies within the code.
    /// It provides properties for different types of SQL objects that can be present in the code, such as tables, views, and stored procedures.
    /// It also provides methods to convert the dependencies to comma-separated strings.
    /// </remarks>
    internal class DependencyParser
    {
        internal List<string> SelectTables { get; } = new();
        internal List<string> UpdateTables { get; } = new();
        internal List<string> DeleteTables { get; } = new();
        internal List<string> CreateTables { get; } = new();
        internal List<string> DropTables { get; } = new();
        internal List<string> InsertTables { get; } = new();
        internal List<string> CreateViews { get; } = new();
        internal List<string> StoredProcedures { get; } = new();

        internal List<string> BeforeDependencyObjects { get; }
        internal List<string> AfterDependencyObjects { get; }
        internal HashSet<string> CTETables { get; } = new();

        /// <summary>
        /// Gets a comma-separated string of the objects that the SQL code depends on before execution.
        /// </summary>
        /// <value>
        /// A string that represents the objects that the SQL code depends on before execution. Each object is separated by a comma.
        /// </value>
        /// <remarks>
        /// This property is used to easily represent the objects that the SQL code depends on before execution in a single string format.
        /// It is particularly useful when you want to quickly view or log the dependencies without iterating through the list.
        /// </remarks>
        internal string BeforeDependencyObjectsString => string.Join(",", BeforeDependencyObjects);


        /// <summary>
        /// Gets a comma-separated string representation of the SQL objects that are dependent on the current SQL object after its execution.
        /// </summary>
        /// <value>
        /// A string that represents the SQL objects that are dependent on the current SQL object after its execution. The SQL objects are represented as a comma-separated string.
        /// </value>
        /// <remarks>
        /// This property is used to get a string representation of the SQL objects that are dependent on the current SQL object after its execution. The SQL objects are represented as a comma-separated string.
        /// </remarks>
        //add a method to convert AfterDependencyObjects to comma separated string  
        internal string AfterDependencyObjectsString => string.Join(",", AfterDependencyObjects);

        /// <summary>
        /// Initializes a new instance of the <see cref="DependencyParser"/> class.
        /// </summary>
        /// <param name="defaultDatabase">The default database to be used.</param>
        /// <param name="defaultSchema">The default schema to be used.</param>
        /// <param name="srcCode">The source code to be parsed.</param>
        /// <param name="baseObjectName">The base object name to be used.</param>
        /// <param name="isDependencyObject">Indicates whether the object is a dependency object.</param>
        /// <remarks>
        /// This constructor parses the provided source code and extracts the dependencies if the object is a dependency object.
        /// The extracted dependencies include tables, views, and stored procedures used in select, update, delete, create, drop, and insert statements.
        /// </remarks>
        internal DependencyParser(string defaultDatabase, string defaultSchema, string srcCode, string baseObjectName, bool isDependencyObject)
        {
            if (isDependencyObject)
            {
                TSqlParser parser = ParserForSql.GetParser(srcCode);
                IList<ParseError> errors;
                var script = parser.Parse(new System.IO.StringReader(srcCode), out errors) as TSqlScript;

                DependencyExtractor visitor = new DependencyExtractor(defaultDatabase, defaultSchema, baseObjectName);
                if (script != null) script.Accept(visitor);

                SelectTables = visitor.SelectTables.ToList();
                UpdateTables = visitor.UpdateTables.ToList();
                DeleteTables = visitor.DeleteTables.ToList();
                CreateTables = visitor.CreateTables.ToList();
                DropTables = visitor.DropTables.ToList();
                InsertTables = visitor.InsertTables.ToList();
                CreateViews = visitor.CreateViews.ToList();
                StoredProcedures = visitor.StoredProcedures.ToList();
                BeforeDependencyObjects = visitor.BeforeDependencyObjects.ToList();
                AfterDependencyObjects = visitor.AfterDependencyObjects.ToList();
                CTETables = visitor.CTETables;
            }

        }

    }
}
