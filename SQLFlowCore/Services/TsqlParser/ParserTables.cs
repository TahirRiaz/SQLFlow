using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// The ParserTables class is an internal class in the SQLFlowCore.Engine.TSQLParser namespace.
    /// This class is responsible for parsing T-SQL scripts and extracting table references from them.
    /// </summary>
    /// <remarks>
    /// The main functionality of this class is provided by the GetReferencedTables method, which takes a T-SQL script as input,
    /// parses it, and returns a list of table references found in the script.
    /// This class also contains a nested class, TableReferenceVisitor, which is a TSqlFragmentVisitor. 
    /// This visitor is used to visit different parts of the T-SQL script and collect table references.
    /// </remarks>
    internal class ParserTables
    {
        /// <summary>
        /// Parses the provided T-SQL script and extracts the names of the tables referenced in it.
        /// </summary>
        /// <param name="tsqlScript">The T-SQL script to parse.</param>
        /// <returns>A list of table names referenced in the provided T-SQL script.</returns>
        /// <exception cref="System.Exception">Thrown when there are errors encountered while parsing the script.</exception>
        internal List<string> GetReferencedTables(string tsqlScript)
        {
            var parser = ParserForSql.GetParser(tsqlScript);
            IList<ParseError> errors;
            var fragment = parser.Parse(new StringReader(tsqlScript), out errors);

            if (errors.Count > 0)
            {
                throw new System.Exception($"Errors encountered while parsing the script: {string.Join(", ", errors)}");
            }

            var tableVisitor = new TableReferenceVisitor();
            fragment.Accept(tableVisitor);

            return tableVisitor.ReferencedTables;
        }

        /// <summary>
        /// The TableReferenceVisitor class is a TSqlFragmentVisitor used to visit different parts of the T-SQL script and collect table references.
        /// </summary>
        /// <remarks>
        /// This class overrides several methods from the TSqlFragmentVisitor base class to visit specific parts of the T-SQL script, such as NamedTableReference, QueryDerivedTable, WithCtesAndXmlNamespaces, and CommonTableExpression.
        /// The visited table references are stored in a HashSet and can be accessed through the ReferencedTables property.
        /// </remarks>
        internal class TableReferenceVisitor : TSqlFragmentVisitor
        {
            private readonly HashSet<string> _referencedTables = new();


            internal List<string> ReferencedTables => new(_referencedTables);

            /// <summary>
            /// Visits the NamedTableReference nodes of the T-SQL script.
            /// </summary>
            /// <param name="namedTableReference">The NamedTableReference node to visit.</param>
            /// <remarks>
            /// This method is overridden from the TSqlFragmentVisitor base class. 
            /// It is used to visit NamedTableReference nodes in the T-SQL script and add the full name of the table reference to the ReferencedTables list.
            /// The full name of the table reference is constructed by joining the identifiers of the SchemaObject of the NamedTableReference node.
            /// </remarks>
            public override void Visit(NamedTableReference namedTableReference)
            {
                if (namedTableReference.SchemaObject != null)
                {
                    string fullName = string.Join(".", namedTableReference.SchemaObject.Identifiers.Select(id => id.Value));
                    _referencedTables.Add(fullName);
                }
            }

            /// <summary>
            /// Visits the QueryDerivedTable nodes of the T-SQL script.
            /// </summary>
            /// <param name="queryDerivedTable">The QueryDerivedTable node to visit.</param>
            /// <remarks>
            /// This method is overridden from the TSqlFragmentVisitor base class. 
            /// It is used to visit QueryDerivedTable nodes in the T-SQL script. 
            /// The method currently does not perform any additional operations beyond the base class implementation.
            /// Future enhancements may include collecting additional information from QueryDerivedTable nodes.
            /// </remarks>
            public override void Visit(QueryDerivedTable queryDerivedTable)
            {
                base.Visit(queryDerivedTable);
            }

            /// <summary>
            /// Visits the WithCtesAndXmlNamespaces nodes of the T-SQL script.
            /// </summary>
            /// <param name="withCtesAndXmlNamespaces">The WithCtesAndXmlNamespaces node to visit.</param>
            /// <remarks>
            /// This method is overridden from the TSqlFragmentVisitor base class. 
            /// It is used to visit WithCtesAndXmlNamespaces nodes in the T-SQL script. 
            /// The method iterates over each CommonTableExpression in the WithCtesAndXmlNamespaces node and accepts this visitor for further processing.
            /// After processing all CommonTableExpressions, it calls the base class implementation.
            /// </remarks>
            public override void Visit(WithCtesAndXmlNamespaces withCtesAndXmlNamespaces)
            {
                foreach (var cte in withCtesAndXmlNamespaces.CommonTableExpressions)
                {
                    cte.Accept(this);
                }
                base.Visit(withCtesAndXmlNamespaces);
            }

            /// <summary>
            /// Visits the CommonTableExpression nodes of the T-SQL script.
            /// </summary>
            /// <param name="commonTableExpression">The CommonTableExpression node to visit.</param>
            /// <remarks>
            /// This method is overridden from the TSqlFragmentVisitor base class. 
            /// It is used to visit CommonTableExpression nodes in the T-SQL script. 
            /// The method first accepts the QueryExpression of the CommonTableExpression node, allowing it to be visited by this visitor, 
            /// and then calls the base class implementation to continue the visiting process.
            /// </remarks>
            public override void Visit(CommonTableExpression commonTableExpression)
            {
                commonTableExpression.QueryExpression.Accept(this);
                base.Visit(commonTableExpression);
            }
        }
    }
}
