using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.IO;
using System;
using SQLFlowCore.Services.TsqlParser;

/// <summary>
/// The `StoredProcedureHelper` class provides functionality to manipulate and extract information from SQL Stored Procedures.
/// </summary>
/// <remarks>
/// This class contains methods to parse SQL Stored Procedures and extract specific parts of the procedure. 
/// It uses the `ParserForSql` class to parse the SQL and the `CreateProcedureVisitor` class to visit specific nodes in the parsed SQL.
/// </remarks>
internal static class StoredProcedureHelper
{
    /// <summary>
    /// Extracts the portion of the SQL Stored Procedure after the AS statement.
    /// </summary>
    /// <param name="procedureSql">The SQL Stored Procedure as a string.</param>
    /// <returns>A string containing the portion of the SQL Stored Procedure after the AS statement. If the AS statement is not found, an empty string is returned.</returns>
    /// <exception cref="System.Exception">Thrown when parsing errors are encountered in the SQL Stored Procedure.</exception>
    /// <remarks>
    /// This method uses the `ParserForSql.GetParser` method to parse the SQL Stored Procedure and the `CreateProcedureVisitor` class to visit specific nodes in the parsed SQL.
    /// </remarks>
    internal static string ExtractAfterAsStatement(string procedureSql)
    {
        TSqlParser parser = ParserForSql.GetParser(procedureSql);
        IList<ParseError> errors;
        var fragment = parser.Parse(new StringReader(procedureSql), out errors);

        if (errors.Count > 0)
        {
            throw new Exception("Parsing errors encountered in SQL.");
        }

        var visitor = new CreateProcedureVisitor();
        fragment.Accept(visitor);

        if (visitor.CreateProcedureFound && visitor.BodyStartOffset.HasValue)
        {
            int startOffset = visitor.BodyStartOffset.Value;
            return procedureSql.Substring(startOffset);
        }

        return string.Empty;
    }

    /// <summary>
    /// The `CreateProcedureVisitor` class is a visitor that traverses the SQL parse tree to find the 'AS' keyword in a stored procedure.
    /// </summary>
    /// <remarks>
    /// This class is used to identify the start of the body of a stored procedure in SQL. It sets the `BodyStartOffset` property to the position of the token following the 'AS' keyword. 
    /// If the 'AS' keyword is not found, `BodyStartOffset` remains null. The `CreateProcedureFound` property indicates whether a stored procedure has been encountered during the visit.
    /// </remarks>
    private class CreateProcedureVisitor : TSqlFragmentVisitor
    {
        internal int? BodyStartOffset { get; private set; }
        internal bool CreateProcedureFound { get; private set; } = false;

        public override void Visit(CreateProcedureStatement node)
        {
            CreateProcedureFound = true;

            // Iterating through the tokens to find the AS keyword
            var tokens = node.ScriptTokenStream;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].TokenType == TSqlTokenType.As)
                {
                    // Assuming the next token after 'AS' is the start of the body
                    if (i + 1 < tokens.Count)
                    {
                        BodyStartOffset = tokens[i + 1].Offset;
                    }
                    break;
                }
            }
        }

    }

}