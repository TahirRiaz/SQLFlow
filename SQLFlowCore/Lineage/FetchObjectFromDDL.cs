using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using Microsoft.SqlServer.Management.SqlParser.Parser;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SQLFlowCore.Common;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SQLFlowCore.Lineage
{
    internal class FetchObjectFromDDL
    {
        internal SQLObject DDLObject;
        internal string ObjectType = "";
        internal string CurrentDB = "";
        internal UrnCollection UrnCol;

        internal FetchObjectFromDDL(string currentDB, string dmlSQL, UrnCollection urnCollection)
        {
            CurrentDB = currentDB;
            UrnCol = urnCollection;
            Parser.Parse(dmlSQL);
            var processed = ParseSql(dmlSQL);
            var visitor = new FirstCreateStatementVisitor(CurrentDB, UrnCol);
            DDLObject = visitor.CurrentObject;
            if (!processed.errors.Any())
            {
                processed.sqlTree.Accept(visitor);
            }
        }

        private static (TSqlFragment sqlTree, IList<ParseError> errors) ParseSql(string procText)
        {
            var parser = new TSql150Parser(true);
            using (var textReader = new StringReader(procText))
            {
                var sqlTree = parser.Parse(textReader, out var errors);

                return (sqlTree, errors);
            }
        }
    }

    /// <summary>
    /// A visitor that identifies the first CREATE statement in a SQL script
    /// and stops traversing after processing it.
    /// </summary>
    internal class FirstCreateStatementVisitor : TSqlFragmentVisitor
    {
        internal string CurrentDB = "";
        internal SQLObject CurrentObject = null;
        internal UrnCollection UrnCol;
        private bool _foundCreate = false;
        private int _nestingLevel = 0;

        internal FirstCreateStatementVisitor(string currentDB, UrnCollection urnCol)
        {
            CurrentObject = new SQLObject();
            CurrentDB = currentDB;
            UrnCol = urnCol;
        }

        // Only process top-level CREATE statements, not nested ones
        private bool IsTopLevelStatement()
        {
            return _nestingLevel == 0;
        }

        public override void ExplicitVisit(TSqlStatement node)
        {
            // If we've already found a create statement, skip all further processing
            if (_foundCreate)
                return;

            // Increment nesting level to track when we're inside another statement
            _nestingLevel++;
            base.ExplicitVisit(node);
            _nestingLevel--;
        }

        public override void Visit(CreateTableStatement node)
        {
            // Skip if we've already found a CREATE statement or if this is not a top-level statement
            if (_foundCreate || !IsTopLevelStatement())
                return;

            string databaseIdentifier = node?.SchemaObjectName.DatabaseIdentifier?.Value;
            string schemaIdentifier = node?.SchemaObjectName.SchemaIdentifier?.Value;
            string baseIdentifier = node?.SchemaObjectName.BaseIdentifier?.Value;

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            Urn urnVal = FetchLineageDep.GetUrn(UrnCol, databaseIdentifier, schemaIdentifier, baseIdentifier);

            if (baseIdentifier != null && urnVal != null)
            {
                if (baseIdentifier.Trim().Length > 0 && urnVal.Type == "Table")
                {
                    CurrentObject.ObjDatabase = databaseIdentifier;
                    CurrentObject.ObjSchema = schemaIdentifier;
                    CurrentObject.ObjName = baseIdentifier;
                    CurrentObject.ObjFullName = $"[{databaseIdentifier}].[{schemaIdentifier}].[{baseIdentifier}]";
                    CurrentObject.SQLFlowObjectType = "tbl";
                    CurrentObject.ObjUrn = urnVal;
                    _foundCreate = true;
                }
            }
        }

        public override void Visit(CreateViewStatement node)
        {
            // Skip if we've already found a CREATE statement or if this is not a top-level statement
            if (_foundCreate || !IsTopLevelStatement())
                return;

            string databaseIdentifier = node?.SchemaObjectName.DatabaseIdentifier?.Value;
            string schemaIdentifier = node?.SchemaObjectName.SchemaIdentifier?.Value;
            string baseIdentifier = node?.SchemaObjectName.BaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            Urn urnVal = FetchLineageDep.GetUrn(UrnCol, databaseIdentifier, schemaIdentifier, baseIdentifier);

            if (baseIdentifier != null && urnVal != null)
            {
                if (baseIdentifier.Trim().Length > 0 && urnVal.Type == "View")
                {
                    CurrentObject.ObjDatabase = databaseIdentifier;
                    CurrentObject.ObjSchema = schemaIdentifier;
                    CurrentObject.ObjName = baseIdentifier;
                    CurrentObject.ObjFullName = $"[{databaseIdentifier}].[{schemaIdentifier}].[{baseIdentifier}]";
                    CurrentObject.SQLFlowObjectType = "vew";
                    CurrentObject.ObjUrn = urnVal;
                    _foundCreate = true;
                }
            }
        }

        public override void Visit(CreateProcedureStatement node)
        {
            // Skip if we've already found a CREATE statement or if this is not a top-level statement
            if (_foundCreate || !IsTopLevelStatement())
                return;

            string databaseIdentifier = node?.ProcedureReference.Name.DatabaseIdentifier?.Value;
            string schemaIdentifier = node?.ProcedureReference.Name.SchemaIdentifier?.Value;
            string baseIdentifier = node?.ProcedureReference.Name.BaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            Urn urnVal = FetchLineageDep.GetUrn(UrnCol, databaseIdentifier, schemaIdentifier, baseIdentifier);

            if (baseIdentifier != null && urnVal != null)
            {
                if (baseIdentifier.Trim().Length > 0 && urnVal.Type == "StoredProcedure")
                {
                    CurrentObject.ObjDatabase = databaseIdentifier;
                    CurrentObject.ObjSchema = schemaIdentifier;
                    CurrentObject.ObjName = baseIdentifier;
                    CurrentObject.ObjFullName = $"[{databaseIdentifier}].[{schemaIdentifier}].[{baseIdentifier}]";
                    CurrentObject.SQLFlowObjectType = "sp";
                    CurrentObject.ObjUrn = urnVal;
                    _foundCreate = true;
                }
            }
        }

        // Add handlers for any other CREATE statement types you need to support
        // For example: CreateFunctionStatement, CreateIndexStatement, etc.

        internal string getTokenText(TSqlFragment frag)
        {
            var sb = new StringBuilder();
            for (int i = frag.FirstTokenIndex; i <= frag.LastTokenIndex; ++i)
            {
                sb.Append(frag.ScriptTokenStream[i].Text);
            }
            return sb.ToString();
        }
    }
}