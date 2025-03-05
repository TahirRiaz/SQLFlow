using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Provides helper methods for working with SQL views.
    /// </summary>
    /// <remarks>
    /// This class includes methods to extract specific parts of SQL view definitions.
    /// </remarks>
    internal static class ViewHelper
    {
        /// <summary>
        /// Extracts the SQL statement after the "CREATE VIEW AS" part of a SQL view definition.
        /// </summary>
        /// <param name="viewSql">The SQL view definition.</param>
        /// <returns>The SQL statement after the "CREATE VIEW AS" part of the view definition, or an empty string if the view definition does not contain "CREATE VIEW AS".</returns>
        /// <exception cref="Exception">Thrown when there are parsing errors in the SQL view definition.</exception>
        internal static string ExtractAfterCreateViewAs(string viewSql)
        {
            TSqlParser parser = ParserForSql.GetParser(viewSql);
            //var parser = SQLFlowCore.Engine.TSQLParser.GetParser(viewSql);


            IList<ParseError> errors;
            var fragment = parser.Parse(new System.IO.StringReader(viewSql), out errors);

            if (errors.Count > 0)
            {
                throw new Exception("Parsing errors encountered in SQL.");
            }

            var visitor = new CreateViewVisitor();
            fragment.Accept(visitor);

            if (visitor.CreateViewFound && visitor.AfterAsStatement != null)
            {
                int startOffset = visitor.AfterAsStatement.StartOffset;
                int length = visitor.AfterAsStatement.FragmentLength;

                return viewSql.Substring(startOffset, length);
            }

            return string.Empty;
        }

        /// <summary>
        /// The `CreateViewVisitor` class is a visitor that visits nodes in a T-SQL script.
        /// It is used to find and extract the SQL statement after the "CREATE VIEW AS" part of a SQL view definition.
        /// </summary>
        /// <remarks>
        /// This class is used internally by the `ViewHelper` class. It overrides the `Visit` method to find the "CREATE VIEW AS" part of a SQL view definition.
        /// When it finds a `CreateViewStatement`, it sets the `CreateViewFound` property to `true` and stores the SQL statement after "AS" in the `AfterAsStatement` property.
        /// </remarks>
        private class CreateViewVisitor : TSqlFragmentVisitor
        {
            internal TSqlFragment AfterAsStatement { get; private set; }
            internal bool CreateViewFound { get; private set; } = false;

            public override void Visit(CreateViewStatement node)
            {
                CreateViewFound = true;
                AfterAsStatement = node.SelectStatement;
            }
        }
    }
}

