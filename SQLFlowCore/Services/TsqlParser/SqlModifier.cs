using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// The SqlModifier class provides methods for modifying SQL scripts.
    /// </summary>
    /// <remarks>
    /// This class includes methods for extending WHERE clauses in SQL scripts and for getting the corresponding SQL script generator based on the parser type.
    /// </remarks>
    internal class SqlModifier
    {
        /// <summary>
        /// Extends the WHERE clauses in the provided SQL script with additional criteria.
        /// </summary>
        /// <param name="sqlScript">The SQL script to be modified.</param>
        /// <param name="additionalCriteria">The additional criteria to be added to the WHERE clauses.</param>
        /// <returns>A new SQL script with extended WHERE clauses.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the provided SQL script or additional criteria is null or empty.</exception>
        /// <exception cref="InvalidOperationException">Thrown when errors occur while parsing the SQL script or the additional criteria.</exception>
        internal static string ExtendWhereClauses(string sqlScript, string additionalCriteria)
        {
            if (string.IsNullOrEmpty(sqlScript))
            {
                throw new ArgumentNullException(nameof(sqlScript), "SQL script cannot be null or empty.");
            }

            if (string.IsNullOrEmpty(additionalCriteria))
            {
                throw new ArgumentNullException(nameof(additionalCriteria), "additionalCriteria criteria cannot be null or empty.");
            }

            var parser = ParserForSql.GetParser(sqlScript);
            IList<ParseError> errors;
            TSqlFragment fragment;

            using (var reader = new StringReader(sqlScript))
            {
                fragment = parser.Parse(reader, out errors);
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Errors occurred while parsing the SQL script. First error: {errors[0].Message}");
            }

            //var visitor = new WhereClauseVisitor();
            //fragment.Accept(visitor);

            CustomTSqlVisitor visitor = new CustomTSqlVisitor();
            fragment.Accept(visitor);

            // Directly parse the additional criteria into a BooleanExpression
            TSqlFragment parsedFragment;
            using (var reader = new StringReader($"SELECT * FROM dummy WHERE {additionalCriteria}"))
            {
                parsedFragment = parser.Parse(reader, out errors);
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Errors occurred while parsing the additional criteria. First error: {errors[0].Message}");
            }


            var selectStatement = GetSelectStatementFromFragment(parsedFragment);
            var querySpecification = selectStatement?.QueryExpression as QuerySpecification;
            var additionalBoolExpression = querySpecification?.WhereClause?.SearchCondition;

            foreach (var querySpec in visitor.QuerySpecifications)
            {
                if (querySpec.WhereClause == null)
                {
                    // Create a new WhereClause for this QuerySpecification
                    querySpec.WhereClause = new WhereClause
                    {
                        SearchCondition = additionalBoolExpression
                    };
                }
                else
                {
                    // If there's already a WhereClause, append the additionalCriteria to it
                    var newCondition = new BooleanBinaryExpression
                    {
                        FirstExpression = querySpec.WhereClause.SearchCondition,
                        BinaryExpressionType = BooleanBinaryExpressionType.And,
                        SecondExpression = additionalBoolExpression
                    };

                    querySpec.WhereClause.SearchCondition = newCondition;
                }
            }

            /*
            foreach (var whereClause in visitor.WhereClauses)
            {
                // Merge the existing WHERE clause with the new condition
                var newCondition = new BooleanBinaryExpression
                {
                    FirstExpression = whereClause.SearchCondition,
                    BinaryExpressionType = BooleanBinaryExpressionType.And,
                    SecondExpression = additionalBoolExpression // This will directly append the additionalCriteria without treating it as a string literal
                };
                whereClause.SearchCondition = newCondition;
            }*/

            var scriptGenerator = GetCorrespondingScriptGenerator(parser);
            string newScript;
            scriptGenerator.GenerateScript(fragment, out newScript);
            return newScript;
        }

        /// <summary>
        /// Gets the corresponding SQL script generator based on the provided T-SQL parser.
        /// </summary>
        /// <param name="parser">The T-SQL parser for which the corresponding script generator is to be retrieved.</param>
        /// <returns>The SQL script generator that corresponds to the provided T-SQL parser.</returns>
        /// <exception cref="ArgumentNullException">Thrown when the provided parser is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the parser version is unsupported or unrecognized.</exception>
        internal static SqlScriptGenerator GetCorrespondingScriptGenerator(TSqlParser parser)
        {
            if (parser == null)
            {
                throw new ArgumentNullException(nameof(parser));
            }

            string parserTypeName = parser.GetType().Name;
            SqlScriptGenerator scriptGenerator;

            if (parserTypeName.Contains("160"))
            {
                scriptGenerator = new Sql160ScriptGenerator();
            }
            else if (parserTypeName.Contains("150"))
            {
                scriptGenerator = new Sql150ScriptGenerator();
            }
            else if (parserTypeName.Contains("140"))
            {
                scriptGenerator = new Sql140ScriptGenerator();
            }
            else if (parserTypeName.Contains("130"))
            {
                scriptGenerator = new Sql130ScriptGenerator();
            }
            else if (parserTypeName.Contains("120"))
            {
                scriptGenerator = new Sql120ScriptGenerator();
            }
            else if (parserTypeName.Contains("110"))
            {
                scriptGenerator = new Sql110ScriptGenerator();
            }
            else if (parserTypeName.Contains("100"))
            {
                scriptGenerator = new Sql100ScriptGenerator();
            }
            else if (parserTypeName.Contains("90"))
            {
                scriptGenerator = new Sql90ScriptGenerator();
            }
            else
            {
                throw new InvalidOperationException($"Unsupported or unrecognized parser version: {parserTypeName}");
            }

            return scriptGenerator;
        }

        /// <summary>
        /// Retrieves the SelectStatement from a given TSqlFragment.
        /// </summary>
        /// <param name="fragment">The TSqlFragment to extract the SelectStatement from.</param>
        /// <returns>
        /// The SelectStatement if found within the fragment, otherwise null. 
        /// If the fragment is a TSqlScript, it iterates through each batch and statement to find a SelectStatement.
        /// </returns>
        private static SelectStatement GetSelectStatementFromFragment(TSqlFragment fragment)
        {
            if (fragment is SelectStatement selectStatement)
            {
                return selectStatement;
            }
            else if (fragment is TSqlScript script)
            {
                foreach (var batch in script.Batches)
                {
                    foreach (var statement in batch.Statements)
                    {
                        if (statement is SelectStatement)
                        {
                            return statement as SelectStatement;
                        }
                    }
                }
            }

            return null;
        }


    }

    /// <summary>
    /// The CustomTSqlVisitor class is a custom implementation of the TSqlConcreteFragmentVisitor.
    /// </summary>
    /// <remarks>
    /// This class is used to visit QuerySpecification nodes in a T-SQL script. It collects all QuerySpecification nodes in a List for further processing.
    /// </remarks>
    public class CustomTSqlVisitor : TSqlConcreteFragmentVisitor
    {
        internal List<QuerySpecification> QuerySpecifications { get; } = new();

        public override void ExplicitVisit(QuerySpecification node)
        {
            QuerySpecifications.Add(node);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>
    /// The WhereClauseVisitor class is a custom implementation of the TSqlFragmentVisitor.
    /// </summary>
    /// <remarks>
    /// This class is used to visit WhereClause nodes in a T-SQL script. It collects all WhereClause nodes in a List for further processing.
    /// </remarks>
    public class WhereClauseVisitor : TSqlFragmentVisitor
    {
        internal List<WhereClause> WhereClauses { get; private set; } = new();

        public override void Visit(WhereClause node)
        {
            WhereClauses.Add(node);
            base.Visit(node);
        }
    }

}


