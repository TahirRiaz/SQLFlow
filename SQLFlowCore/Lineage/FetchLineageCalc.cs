using Microsoft.SqlServer.Management.SqlParser.Parser;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace SQLFlowCore.Lineage
{
    internal class FetchLineageCalc
    {
        internal string dmlSQL = "";
        internal List<string> BeforeDependency = new();
        internal List<string> AfterDependency = new();
        internal string ObjectType = "";
        internal string CurrentDB = "";
        internal string DefaultDB = "";
        internal bool IsDependencyObject = false;
        internal List<string> BeforeDependencyObjects { get; }
        internal List<string> AfterDependencyObjects { get; }

        internal FetchLineageCalc(string currentDB, string schemaName, string objectName, bool IsDependencyObject, string dmlSQL)
        {
            CurrentDB = currentDB;

            if (IsDependencyObject == true)
            {
                Parser.Parse(dmlSQL);
                var processed = ParseSql(dmlSQL);
                var visitor = new StatsVisitor3(CurrentDB);

                if (!processed.errors.Any())
                    processed.sqlTree.Accept(visitor);

                visitor.CleanupDependency(schemaName, objectName);

                BeforeDependencyObjects = visitor.BeforeDependencyTables;
                AfterDependencyObjects = visitor.AfterDependencyTables;

                //BeforeDependency = string.Join(",", visitor.BeforeDependencyTables);
                //AfterDependency = string.Join(",", visitor.AfterDependencyTables);

                BeforeDependency = visitor.BeforeDependencyTables;
                AfterDependency = visitor.AfterDependencyTables;

            }
            else
            {
                BeforeDependencyObjects = new List<string>();
                AfterDependencyObjects = new List<string>();
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

    internal class StatsVisitor3 : TSqlFragmentVisitor
    {

        internal string CurrentDB = "";
        internal int BeforeDependencyCounter { get; private set; }
        internal int QueryDerivedTableCounter { get; private set; }
        internal int StoredProcedureCounter { get; private set; }
        internal int QualifiedJoinCounter { get; private set; }
        internal int UnQualifiedJoinCounter { get; private set; }
        internal int CteCounter { get; private set; }
        internal int SelectCounter { get; private set; }
        internal int AfterDependencyCounter { get; private set; }
        internal int UpdateCounter { get; private set; }
        internal int DeleteCounter { get; private set; }
        internal int CreateCounter { get; private set; }
        internal int DropCounter { get; private set; }

        internal List<string> BeforeDependencyTables { get; }
        internal List<string> QueryDerivedTables { get; }
        internal List<string> StoredProcedureTables { get; }
        internal List<string> UnQualifiedJoinTables { get; }
        internal List<string> QualifiedJoinTables { get; }

        internal List<string> SelectTables { get; }
        internal List<string> AfterDependencyTables { get; }
        internal List<string> UpdateTables { get; }
        internal List<string> DeleteTables { get; }
        internal List<string> CreateTables { get; }
        internal List<string> DropTables { get; }
        internal List<string> CTETables { get; }

        internal StatsVisitor3(string currentDB)
        {
            BeforeDependencyTables = new List<string>();
            QueryDerivedTables = new List<string>();
            UnQualifiedJoinTables = new List<string>();
            QualifiedJoinTables = new List<string>();
            SelectTables = new List<string>();
            AfterDependencyTables = new List<string>();
            UpdateTables = new List<string>();
            DeleteTables = new List<string>();
            CreateTables = new List<string>();
            DropTables = new List<string>();
            CTETables = new List<string>();
            StoredProcedureTables = new List<string>();
            CurrentDB = currentDB;
        }

        internal void CleanupDependency(string schemaName, string objectName)
        {
            for (int x = 0; x < BeforeDependencyTables.Count; x++)
            {
                if (FetchLineageDep.CompareObjects(BeforeDependencyTables[x], schemaName, objectName))
                {
                    BeforeDependencyTables.Remove(BeforeDependencyTables[x]);
                }
            }

            for (int x = 0; x < AfterDependencyTables.Count; x++)
            {
                if (FetchLineageDep.CompareObjects(AfterDependencyTables[x], schemaName, objectName))
                {
                    AfterDependencyTables.Remove(AfterDependencyTables[x]);
                }
            }

            foreach (string obj in BeforeDependencyTables)
            {
                AfterDependencyTables.Remove(obj);
            }

            //foreach (string obj in AfterDependencyObjects)
            //{
            //    BeforeDependencyObjects.Remove(obj);
            //}

            foreach (string obj in CTETables)
            {
                AfterDependencyTables.Remove(obj);
            }

            foreach (string obj in CTETables)
            {
                BeforeDependencyTables.Remove(obj);
            }
        }

        public override void Visit(QuerySpecification node)
        {
            QuerySpecification querySpecification = node;
            if (querySpecification != null)
            {
                FromClause fromClause = querySpecification.FromClause;
                if (fromClause != null)
                {
                    NamedTableReference namedTableReference = fromClause.TableReferences[0] as NamedTableReference;
                    foreach (TableReference tbr in fromClause.TableReferences)
                    {
                        string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                        string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                        string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

                        if (schemaIdentifier == null)
                        {
                            schemaIdentifier = "dbo";
                        }

                        if (databaseIdentifier == null)
                        {
                            databaseIdentifier = CurrentDB;
                        }

                        if (baseIdentifier != null)
                        {
                            if (baseIdentifier.Trim().Length > 0)
                            {
                                string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                                string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                                string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                                if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                                {
                                    BeforeDependencyTables.Add(objName1);
                                    BeforeDependencyCounter++;
                                }
                                SelectTables.Add(objName1);
                                SelectCounter++;
                            }

                        }
                    }

                }

            }
        }

        public override void Visit(SelectStatement node)
        {
            if (node.Into != null)
            {
                string baseIdentifier = node.Into.BaseIdentifier?.Value;
                string schemaIdentifier = node.Into.SchemaIdentifier?.Value;
                string databaseIdentifier = node.Into.DatabaseIdentifier?.Value;

                if (schemaIdentifier == null)
                {
                    schemaIdentifier = "dbo";
                }

                if (databaseIdentifier == null)
                {
                    databaseIdentifier = CurrentDB;
                }

                if (baseIdentifier != null)
                {
                    if (baseIdentifier.Trim().Length > 0)
                    {
                        string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                        string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                        string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                        if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                        {

                            BeforeDependencyTables.Add(objName1);
                            BeforeDependencyCounter++;

                        }
                        SelectTables.Add(objName1);
                        SelectCounter++;
                    }


                }
            }

            QuerySpecification querySpecification = node.QueryExpression as QuerySpecification;
            if (querySpecification != null)
            {
                FromClause fromClause = querySpecification.FromClause;

                if (fromClause != null)
                {
                    NamedTableReference namedTableReference = fromClause.TableReferences[0] as NamedTableReference;
                    foreach (TableReference tbr in fromClause.TableReferences)
                    {
                        string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                        string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                        string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

                        if (schemaIdentifier == null)
                        {
                            schemaIdentifier = "dbo";
                        }

                        if (databaseIdentifier == null)
                        {
                            databaseIdentifier = CurrentDB;
                        }

                        if (baseIdentifier != null)
                        {
                            if (baseIdentifier.Trim().Length > 0)
                            {
                                string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                                string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                                string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                                if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                                {

                                    BeforeDependencyTables.Add(objName1);
                                    BeforeDependencyCounter++;

                                }
                                SelectTables.Add(objName1);
                                SelectCounter++;
                            }

                        }
                    }
                }

            }
        }

        public override void Visit(QualifiedJoin node)
        {
            NamedTableReference namedTableReference = node.FirstTableReference as NamedTableReference;
            string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
            string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
            string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

            NamedTableReference namedTableReference2 = node.SecondTableReference as NamedTableReference;
            string baseIdentifier2 = namedTableReference2?.SchemaObject.BaseIdentifier?.Value;
            string schemaIdentifier2 = namedTableReference2?.SchemaObject.SchemaIdentifier?.Value;
            string databaseIdentifier2 = namedTableReference2?.SchemaObject.DatabaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (schemaIdentifier2 == null)
            {
                schemaIdentifier2 = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (databaseIdentifier2 == null)
            {
                databaseIdentifier2 = CurrentDB;
            }


            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                    if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {

                        BeforeDependencyTables.Add(objName1);
                        BeforeDependencyCounter++;

                    }
                    QualifiedJoinTables.Add(objName1);
                    QualifiedJoinCounter++;
                }


            }

            if (baseIdentifier2 != null)
            {
                if (baseIdentifier2.Trim().Length > 0)
                {
                    string dbName2 = databaseIdentifier2 != null ? "[" + databaseIdentifier2 + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName2 = schemaIdentifier2 != null ? "[" + schemaIdentifier2 + "]." : "[dbo].";
                    string objName2 = (dbName2.Length > 3 ? dbName2 : "") + schName2 + (baseIdentifier2 != null ? "[" + baseIdentifier2 + "]" : "");
                    if (BeforeDependencyTables.Contains(objName2) == false && objName2.Contains("#") == false)
                    {

                        BeforeDependencyTables.Add(objName2);
                        BeforeDependencyCounter++;

                    }
                    QualifiedJoinTables.Add(objName2);
                    QualifiedJoinCounter++;
                }


            }
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            string baseIdentifier = node.ProcedureReference.ProcedureReference.Name.BaseIdentifier?.Value;
            string schemaIdentifier = node.ProcedureReference.ProcedureReference.Name.SchemaIdentifier?.Value;
            string databaseIdentifier = node.ProcedureReference.ProcedureReference.Name.DatabaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                    if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {

                        BeforeDependencyTables.Add(objName1);
                        BeforeDependencyCounter++;

                    }
                    StoredProcedureTables.Add(objName1);
                    StoredProcedureCounter++;
                }


            }
        }

        public override void Visit(CommonTableExpression node)
        {
            string objName1 = node.ExpressionName.Value != null ? "[" + node.ExpressionName.Value + "]" : "";

            if (BeforeDependencyTables.Contains(objName1) == true)
            {
                BeforeDependencyTables.Remove(objName1);
                BeforeDependencyCounter = BeforeDependencyCounter - 1;
            }
            CTETables.Add(objName1);
            CteCounter++;
        }

        public override void Visit(QueryDerivedTable node)
        {
            ParseQueryDrivedTable(node);
        }



        void ParseQueryDrivedTable(QueryDerivedTable node)
        {
            QuerySpecification querySpecification = node.QueryExpression as QuerySpecification;
            FromClause fromClause = querySpecification?.FromClause;
            if (fromClause != null)
            {
                NamedTableReference namedTableReference = fromClause.TableReferences[0] as NamedTableReference; //TODO Looks wrong

                foreach (TableReference tbr in fromClause.TableReferences)
                {
                    //NamedTableReference namedTableReference = tbr as NamedTableReference;

                    string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                    string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                    string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

                    if (schemaIdentifier == null)
                    {
                        schemaIdentifier = "dbo";
                    }

                    if (databaseIdentifier == null)
                    {
                        databaseIdentifier = CurrentDB;
                    }

                    if (baseIdentifier != null)
                    {
                        if (baseIdentifier.Trim().Length > 0)
                        {
                            string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                            string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                            string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                            if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                            {

                                BeforeDependencyTables.Add(objName1);
                                BeforeDependencyCounter++;

                            }

                            QueryDerivedTables.Add(objName1);
                            QueryDerivedTableCounter++;
                        }


                    }
                }
            }
        }

        public override void Visit(UnqualifiedJoin node)
        {
            ParseUnQualifedJoin(node);
        }

        void ParseUnQualifedJoin(UnqualifiedJoin node)
        {
            NamedTableReference namedTableReference = node.FirstTableReference as NamedTableReference;
            string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
            string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
            string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

            NamedTableReference namedTableReference2 = node.SecondTableReference as NamedTableReference;
            string baseIdentifier2 = namedTableReference2?.SchemaObject.BaseIdentifier?.Value;
            string schemaIdentifier2 = namedTableReference2?.SchemaObject.SchemaIdentifier?.Value;
            string databaseIdentifier2 = namedTableReference2?.SchemaObject.DatabaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (schemaIdentifier2 != null)
            {
                schemaIdentifier2 = "dbo";
            }


            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (databaseIdentifier2 == null)
            {
                databaseIdentifier2 = CurrentDB;
            }



            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                    if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {

                        BeforeDependencyTables.Add(objName1);
                        BeforeDependencyCounter++;

                    }
                    UnQualifiedJoinTables.Add(objName1);
                    UnQualifiedJoinCounter++;
                }


            }

            if (baseIdentifier2 != null)
            {
                if (baseIdentifier2.Trim().Length > 0)
                {
                    string dbName2 = databaseIdentifier2 != null ? "[" + databaseIdentifier2 + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName2 = schemaIdentifier2 != null ? "[" + schemaIdentifier2 + "]." : "[dbo].";
                    string objName2 = (dbName2.Length > 3 ? dbName2 : "") + schName2 + (baseIdentifier2 != null ? "[" + baseIdentifier2 + "]" : "");
                    if (BeforeDependencyTables.Contains(objName2) == false && objName2.Contains("#") == false)
                    {

                        BeforeDependencyTables.Add(objName2);
                        BeforeDependencyCounter++;

                    }
                    UnQualifiedJoinTables.Add(objName2);
                    UnQualifiedJoinCounter++;
                }


            }

        }


        public override void Visit(InsertStatement node)
        {
            NamedTableReference namedTableReference = node.InsertSpecification.Target as NamedTableReference;
            string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
            string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
            string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                    //if (BeforeDependencyObjects.Contains(objName1) == false)
                    //{
                    //    BeforeDependencyObjects.Add(objName1);
                    //    BeforeDependencyCounter++;
                    //}

                    if (AfterDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {
                        AfterDependencyTables.Add(objName1);
                        AfterDependencyCounter++;
                    }
                }
            }
        }


        public override void Visit(CreateTableStatement node)
        {
            string baseIdentifier = node?.SchemaObjectName.BaseIdentifier?.Value;
            string schemaIdentifier = node?.SchemaObjectName.SchemaIdentifier?.Value;
            string databaseIdentifier = node?.SchemaObjectName.DatabaseIdentifier?.Value;


            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                    //if (BeforeDependencyObjects.Contains(objName1) == false)
                    //{
                    //    BeforeDependencyObjects.Add(objName1);
                    //    BeforeDependencyCounter++;
                    //}

                    if (AfterDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {
                        AfterDependencyTables.Add(objName1);
                        AfterDependencyCounter++;
                    }
                }
            }
        }





        public override void Visit(UpdateStatement node)
        {
            NamedTableReference updTrg = node.UpdateSpecification.Target as NamedTableReference;
            string updTrgName = updTrg?.SchemaObject.BaseIdentifier?.Value;

            if (node.UpdateSpecification.FromClause != null)
            {
                foreach (var RefObj in node.UpdateSpecification.FromClause.TableReferences)
                {
                    Type objType = RefObj.GetType();

                    string baseIdentifier = "";
                    string schemaIdentifier = "";
                    string databaseIdentifier = "";
                    NamedTableReference namedTableReference = null;
                    TableReferenceWithAlias tableReferenceWithAlias = null;

                    if (objType.Name == "UnqualifiedJoin")
                    {
                        UnqualifiedJoin tmpObj = RefObj as UnqualifiedJoin;
                        namedTableReference = tmpObj.FirstTableReference as NamedTableReference;

                        if (namedTableReference == null)
                        {
                            namedTableReference = tmpObj.SecondTableReference as NamedTableReference;
                        }
                        tableReferenceWithAlias = namedTableReference as TableReferenceWithAlias;
                        baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                        schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                        databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;
                    }

                    if (objType.Name == "NamedTableReference")
                    {
                        namedTableReference = RefObj as NamedTableReference;

                        tableReferenceWithAlias = namedTableReference as TableReferenceWithAlias;
                        baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                        schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                        databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;
                    }

                    if (schemaIdentifier == null)
                    {
                        schemaIdentifier = "dbo";
                    }

                    if (databaseIdentifier == null)
                    {
                        databaseIdentifier = CurrentDB;
                    }


                    if (baseIdentifier != null)
                    {
                        if (baseIdentifier.Trim().Length > 0)
                        {
                            string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                            string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                            string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                            if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                            {

                                BeforeDependencyTables.Add(objName1);
                                BeforeDependencyCounter++;

                            }

                            if (tableReferenceWithAlias != null)
                            {
                                if (tableReferenceWithAlias.Alias != null)
                                {
                                    if (tableReferenceWithAlias.Alias.Value == updTrgName)
                                    {
                                        UpdateTables.Add(objName1);
                                        UpdateCounter++;
                                    }
                                }
                            }
                        }



                    }

                }


            }
            else
            {
                string baseIdentifier = updTrg?.SchemaObject.BaseIdentifier?.Value;
                string schemaIdentifier = updTrg?.SchemaObject.SchemaIdentifier?.Value;
                string databaseIdentifier = updTrg?.SchemaObject.DatabaseIdentifier?.Value;

                if (schemaIdentifier == null)
                {
                    schemaIdentifier = "dbo";
                }

                if (databaseIdentifier == null)
                {
                    databaseIdentifier = CurrentDB;
                }

                if (baseIdentifier != null)
                {
                    if (baseIdentifier.Trim().Length > 0)
                    {
                        string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                        string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                        string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                        if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                        {

                            BeforeDependencyTables.Add(objName1);
                            BeforeDependencyCounter++;

                        }

                        UpdateTables.Add(objName1);
                        UpdateCounter++;
                    }
                }
            }

        }

        public override void Visit(DeleteStatement node)
        {
            NamedTableReference delTrg = node.DeleteSpecification.Target as NamedTableReference;
            if (delTrg != null)
            {
                string delTrgName = delTrg.SchemaObject.BaseIdentifier?.Value;

                if (node.DeleteSpecification.FromClause != null)
                {
                    foreach (NamedTableReference namedTableReference in node.DeleteSpecification.FromClause.TableReferences)
                    {
                        TableReferenceWithAlias tableReferenceWithAlias = namedTableReference as TableReferenceWithAlias;

                        string baseIdentifier = namedTableReference?.SchemaObject.BaseIdentifier?.Value;
                        string schemaIdentifier = namedTableReference?.SchemaObject.SchemaIdentifier?.Value;
                        string databaseIdentifier = namedTableReference?.SchemaObject.DatabaseIdentifier?.Value;

                        if (schemaIdentifier == null)
                        {
                            schemaIdentifier = "dbo";
                        }

                        if (databaseIdentifier == null)
                        {
                            databaseIdentifier = CurrentDB;
                        }


                        if (baseIdentifier != null)
                        {
                            if (baseIdentifier.Trim().Length > 0)
                            {
                                string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                                string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                                string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");

                                if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                                {
                                    if (objName1.Length > 3)
                                    {
                                        BeforeDependencyTables.Add(objName1);
                                        BeforeDependencyCounter++;
                                    }
                                }

                                if (tableReferenceWithAlias.Alias != null)
                                {
                                    if (tableReferenceWithAlias.Alias.Value == delTrgName)
                                    {
                                        DeleteTables.Add(objName1);
                                        DeleteCounter++;
                                    }
                                }
                            }
                        }
                    }

                }
                else
                {
                    string baseIdentifier = delTrg?.SchemaObject.BaseIdentifier?.Value;
                    string schemaIdentifier = delTrg?.SchemaObject.SchemaIdentifier?.Value;
                    string databaseIdentifier = delTrg?.SchemaObject.DatabaseIdentifier?.Value;

                    if (schemaIdentifier == null)
                    {
                        schemaIdentifier = "dbo";
                    }

                    if (databaseIdentifier == null)
                    {
                        databaseIdentifier = CurrentDB;
                    }

                    if (baseIdentifier != null)
                    {
                        if (baseIdentifier.Trim().Length > 0)
                        {
                            string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                            string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                            string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                            if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                            {

                                BeforeDependencyTables.Add(objName1);
                                BeforeDependencyCounter++;

                            }

                            DeleteTables.Add(objName1);
                            DeleteCounter++;
                        }


                    }
                }
            }
        }

        public override void Visit(CreateViewStatement node)
        {
            string baseIdentifier = node?.SchemaObjectName.BaseIdentifier?.Value;
            string schemaIdentifier = node?.SchemaObjectName.SchemaIdentifier?.Value;
            string databaseIdentifier = node?.SchemaObjectName.DatabaseIdentifier?.Value;

            if (schemaIdentifier == null)
            {
                schemaIdentifier = "dbo";
            }

            if (databaseIdentifier == null)
            {
                databaseIdentifier = CurrentDB;
            }

            if (baseIdentifier != null)
            {
                if (baseIdentifier.Trim().Length > 0)
                {
                    string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                    string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "[dbo].";
                    string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");

                    if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
                    {
                        BeforeDependencyTables.Add(objName1);
                        BeforeDependencyCounter++;
                    }
                }
            }
        }


        //public override void Visit(ExecutableProcedureReference node)
        //{
        //    string a = node.ToString();
        //    //ExecuteSpecification executeSpec = node.ExecuteSpecification;
        //    //ExecutableProcedureReference executableEntity = (ExecutableProcedureReference)executeSpec.ExecutableEntity;
        //    //var tokenText = getTokenText(executableEntity.ProcedureReference);

        //    //string baseIdentifier = node?..BaseIdentifier?.Value;
        //    //string schemaIdentifier = node?.SchemaObjectName.SchemaIdentifier?.Value;
        //    //string databaseIdentifier = node?.SchemaObjectName.DatabaseIdentifier?.Value;

        //    //if (schemaIdentifier == null)
        //    //{
        //    //    schemaIdentifier = "dbo";
        //    //}

        //    //if (databaseIdentifier == null)
        //    //{
        //    //    databaseIdentifier = this.CurrentDB;
        //    //}

        //    //if (baseIdentifier != null)
        //    //{
        //    //    if (baseIdentifier.Trim().Length > 0)
        //    //    {
        //    //        string dbName1 = ((databaseIdentifier != null) ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]");
        //    //        string schName1 = ((schemaIdentifier != null) ? "[" + schemaIdentifier + "]." : "[dbo].");
        //    //        string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + ((baseIdentifier != null) ? "[" + baseIdentifier + "]" : "");

        //    //        if (BeforeDependencyTables.Contains(objName1) == false && objName1.Contains("#") == false)
        //    //        {
        //    //            BeforeDependencyTables.Add(objName1);
        //    //            BeforeDependencyCounter++;
        //    //        }
        //    //    }
        //    //}
        //}

        internal string getTokenText(TSqlFragment frag)
        {
            var sb = new StringBuilder();
            for (int i = frag.FirstTokenIndex; i <= frag.LastTokenIndex; ++i)
            {
                sb.Append(frag.ScriptTokenStream[i].Text);
            }
            return sb.ToString();

        }


        public override void Visit(DropTableStatement node)
        {
            foreach (SchemaObjectName obj in node.Objects)
            {
                string baseIdentifier = obj.BaseIdentifier?.Value;
                string schemaIdentifier = obj.SchemaIdentifier?.Value;
                string databaseIdentifier = obj.DatabaseIdentifier?.Value;

                if (schemaIdentifier == null)
                {
                    schemaIdentifier = "dbo";
                }

                if (databaseIdentifier == null)
                {
                    databaseIdentifier = CurrentDB;
                }

                if (baseIdentifier != null)
                {
                    if (baseIdentifier.Trim().Length > 0)
                    {
                        string dbName1 = databaseIdentifier != null ? "[" + databaseIdentifier + "]." : $"[{CurrentDB.Replace("[", "").Replace("]", "")}]";
                        string schName1 = schemaIdentifier != null ? "[" + schemaIdentifier + "]." : "";
                        string objName1 = (dbName1.Length > 3 ? dbName1 : "") + schName1 + (baseIdentifier != null ? "[" + baseIdentifier + "]" : "");
                        //DropCounter table is not relevant
                        //if (BeforeDependencyObjects.Contains(objName1) == false)
                        //{
                        //    BeforeDependencyObjects.Add(objName1);
                        //    BeforeDependencyCounter++;
                        //}
                        DropTables.Add(objName1);
                        DropCounter++;
                    }
                }
            }
        }
    }

}
