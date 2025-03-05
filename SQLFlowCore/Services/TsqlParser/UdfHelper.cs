using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System;
using System.IO;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Provides helper methods for working with User-Defined Functions (UDFs) in SQL.
    /// </summary>
    /// <remarks>
    /// This class contains methods to parse SQL functions and extract specific parts of the function definition.
    /// It uses the TSqlParser class from the Microsoft.SqlServer.TransactSql.ScriptDom namespace to parse the SQL.
    /// </remarks>
    internal static class UdfHelper
    {
        /// <summary>
        /// Extracts the part of the SQL function after the AS keyword.
        /// </summary>
        /// <param name="functionSql">The SQL function to parse.</param>
        /// <returns>A string containing the part of the SQL function after the AS keyword. If the AS keyword is not found, an empty string is returned.</returns>
        /// <exception cref="Exception">Thrown when parsing errors are encountered in the SQL function.</exception>
        internal static string ExtractAfterCreateFunctionAs(string functionSql)
        {
            TSqlParser parser = ParserForSql.GetParser(functionSql);
            IList<ParseError> errors;
            var fragment = parser.Parse(new StringReader(functionSql), out errors);

            if (errors.Count > 0)
            {
                throw new Exception("Parsing errors encountered in SQL.");
            }

            var visitor = new CreateFunctionVisitor();
            fragment.Accept(visitor);

            if (visitor.CreateFunctionFound && visitor.AfterAsStatement != null)
            {
                int startOffset = visitor.AfterAsStatement.StartOffset;
                int length = visitor.AfterAsStatement.FragmentLength;

                return functionSql.Substring(startOffset, length);
            }

            return string.Empty;
        }

        /// <summary>
        /// The `CreateFunctionVisitor` class is a visitor that traverses a T-SQL parse tree and extracts information about a CREATE FUNCTION statement.
        /// </summary>
        /// <remarks>
        /// This class is used in conjunction with the `TSqlFragmentVisitor` class from the Microsoft.SqlServer.TransactSql.ScriptDom namespace.
        /// It overrides the `Visit(CreateFunctionStatement node)` method to extract the part of the SQL function after the AS keyword.
        /// </remarks>
        public class CreateFunctionVisitor : TSqlFragmentVisitor
        {
            internal TSqlFragment AfterAsStatement { get; private set; }
            internal bool CreateFunctionFound { get; private set; } = false;

            public override void Visit(CreateFunctionStatement node)
            {
                CreateFunctionFound = true;
                AfterAsStatement = node.ReturnType;
                // Depending on how you want to handle the function body, you might need to adjust this line.
            }
        }
    }
}