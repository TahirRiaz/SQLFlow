using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using SQLFlowCore.Common;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Text.RegularExpressions;
namespace SQLFlowCore.Lineage
{
    internal class FetchLineageDep
    {
        internal static Urn GetUrn(UrnCollection uc, string db, string Sch, string Obj)
        {
            Urn rValue = null;
            foreach (Urn u in uc)
            {
                string uDb = u.XPathExpression.GetAttribute("Name", "Database");
                string uSch = u.XPathExpression.GetAttribute("Schema", u.Type);
                string uObj = u.XPathExpression.GetAttribute("Name", u.Type);

                if (uDb.Equals(db, StringComparison.InvariantCultureIgnoreCase) && uSch.Equals(Sch, StringComparison.InvariantCultureIgnoreCase) && uObj.Equals(Obj, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = u;
                }
            }
            return rValue;
        }

        internal static bool CompareObjects(string srcObject, string compObject)
        {
            bool rValue = false;

            //find parts in srcObject
            string[] sObject = srcObject.Split('.');
            string[] cObject = compObject.Split('.');

            string sDB = "";
            string sSch = "";
            string sObj = "";

            string cDB = "";
            string cSch = "";
            string cObj = "";

            if (sObject.Length == 3)
            {
                sDB = sObject[0].Replace("[", "").Replace("]", "");
                sSch = sObject[1].Replace("[", "").Replace("]", "");
                sObj = sObject[2].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 2)
            {
                sSch = sObject[0].Replace("[", "").Replace("]", "");
                sObj = sObject[1].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 1)
            {
                sObj = sObject[0].Replace("[", "").Replace("]", "");
            }

            if (cObject.Length == 3)
            {
                cDB = cObject[0].Replace("[", "").Replace("]", "");
                cSch = cObject[1].Replace("[", "").Replace("]", "");
                cObj = cObject[2].Replace("[", "").Replace("]", "");
            }
            if (cObject.Length == 2)
            {
                cSch = cObject[0].Replace("[", "").Replace("]", "");
                cObj = cObject[1].Replace("[", "").Replace("]", "");
            }
            if (cObject.Length == 1)
            {
                cObj = cObject[0].Replace("[", "").Replace("]", "");
            }

            if (cObject.Length == 3)
            {
                if (sDB.Equals(cDB, StringComparison.InvariantCultureIgnoreCase) && sSch.Equals(cSch, StringComparison.InvariantCultureIgnoreCase) && sObj.Equals(cObj, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = true;
                }
            }

            if (cObject.Length == 2)
            {
                if (sSch.Equals(cSch, StringComparison.InvariantCultureIgnoreCase) && sObj.Equals(cObj, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = true;
                }
            }

            return rValue;
        }

        internal static bool CompareObjects(string srcObject, string cDB, string cSch, string cObj)
        {
            bool rValue = false;

            //find parts in srcObject
            string[] sObject = srcObject.Split('.');


            string sDB = "";
            string sSch = "";
            string sObj = "";

            if (sObject.Length == 3)
            {
                sDB = sObject[0].Replace("[", "").Replace("]", "");
                sSch = sObject[1].Replace("[", "").Replace("]", "");
                sObj = sObject[2].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 2)
            {
                sSch = sObject[0].Replace("[", "").Replace("]", "");
                sObj = sObject[1].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 1)
            {
                sObj = sObject[0].Replace("[", "").Replace("]", "");
            }

            cDB = cDB.Replace("[", "").Replace("]", "");
            cSch = cSch.Replace("[", "").Replace("]", "");
            cObj = cObj.Replace("[", "").Replace("]", "");

            if (sDB.Equals(cDB, StringComparison.InvariantCultureIgnoreCase) && sSch.Equals(cSch, StringComparison.InvariantCultureIgnoreCase) && sObj.Equals(cObj, StringComparison.InvariantCultureIgnoreCase))
            {
                rValue = true;
            }

            return rValue;
        }

        internal static bool CompareObjects(string srcObject, string cSch, string cObj)
        {
            bool rValue = false;

            //find parts in srcObject
            string[] sObject = srcObject.Split('.');


            string sSch = "";
            string sObj = "";

            if (sObject.Length == 3)
            {
                sSch = sObject[1].Replace("[", "").Replace("]", "");
                sObj = sObject[2].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 2)
            {
                sSch = sObject[0].Replace("[", "").Replace("]", "");
                sObj = sObject[1].Replace("[", "").Replace("]", "");
            }
            if (sObject.Length == 1)
            {
                sObj = sObject[0].Replace("[", "").Replace("]", "");
            }

            cSch = cSch.Replace("[", "").Replace("]", "");
            cObj = cObj.Replace("[", "").Replace("]", "");

            if (sSch.Equals(cSch, StringComparison.InvariantCultureIgnoreCase) && sObj.Equals(cObj, StringComparison.InvariantCultureIgnoreCase))
            {
                rValue = true;
            }

            return rValue;
        }

        internal static bool DupeUrnInCollection(UrnCollection uc, Urn urn)
        {
            bool rValue = false;
            foreach (Urn u in uc)
            {
                string uValue = u.Value.ToLower();
                string urnValue = urn.Value.ToLower();

                if (uValue.Equals(urnValue, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = true;
                }
            }
            return rValue;
        }

        internal static DataRow GetDataRowFromUrn(DataTable tb, Urn urn)
        {
            DataRow rValue = tb.NewRow();

            string uDb = urn.XPathExpression.GetAttribute("Name", "Database");
            string uSch = urn.XPathExpression.GetAttribute("Schema", urn.Type);
            string uObj = urn.XPathExpression.GetAttribute("Name", urn.Type);

            foreach (DataRow dr in tb.Rows)
            {
                string Database = dr["Database"]?.ToString() ?? string.Empty;
                string Schema = dr["Schema"]?.ToString() ?? string.Empty;
                string Object = dr["Object"]?.ToString() ?? string.Empty;

                if (uDb.Equals(Database, StringComparison.InvariantCultureIgnoreCase) && uSch.Equals(Schema, StringComparison.InvariantCultureIgnoreCase) && uObj.Equals(Object, StringComparison.InvariantCultureIgnoreCase))
                {
                    rValue = dr;
                }
            }

            return rValue;
        }

        internal static SQLObject SQLObjectFromUrn(Urn urn)
        {
            string uDb = urn.XPathExpression.GetAttribute("Name", "Database");
            string uSch = urn.XPathExpression.GetAttribute("Schema", urn.Type);
            string uObj = urn.XPathExpression.GetAttribute("Name", urn.Type);
            string ObjFullName = $"[{uDb}].[{uSch}].[{uObj}]";

            SQLObject sQLObject = new SQLObject
            {
                ObjDatabase = uDb,
                ObjSchema = uSch,
                ObjName = uObj,
                ObjFullName = ObjFullName,
                ObjUrn = urn
            };

            return sQLObject;
        }



        internal static string GetSQLFlowObjectType(Urn urn)
        {
            string rValue = "";

            if (urn.Type == "View")
            {
                rValue = "vew";
            }

            if (urn.Type == "Table")
            {
                rValue = "tbl";
            }

            if (urn.Type == "StoredProcedure")
            {
                rValue = "sp";
            }

            return rValue;
        }


        internal static bool IsDependencyObject(Urn urn)
        {
            bool rValue = false;

            if (urn.Type == "View")
            {
                rValue = true;
            }

            if (urn.Type == "StoredProcedure")
            {
                rValue = true;
            }

            return rValue;
        }


        internal static bool IsLineageObject(Urn urn)
        {
            bool rValue = false;

            if (urn.Type == "View")
            {
                rValue = true;
            }

            if (urn.Type == "StoredProcedure")
            {
                rValue = true;
            }

            if (urn.Type == "Table")
            {
                rValue = true;
            }

            return rValue;
        }


        internal static List<SQLObject> BuildSQLObjectFromCollection(string Database, StringCollection stringCollection, UrnCollection allObjects)
        {
            List<SQLObject> rValue = new List<SQLObject>();

            if (stringCollection == null) return rValue;

            foreach (string val in stringCollection)
            {
                if (val.Length > 25)
                {
                    FetchObjectFromDDL fetchOb = new FetchObjectFromDDL(Database, val, allObjects);
                    if (fetchOb.DDLObject != null)
                    {
                        SQLObject o = fetchOb.DDLObject;
                        o.ObjDDL = val;
                        rValue.Add(o);
                    }
                }
            }

            return rValue;
        }

        internal static string GetDDLForUrn(List<SQLObject> list, Urn urn)
        {
            string rValue = "";
            foreach (SQLObject o in list)
            {
                if (o.ObjUrn == urn)
                {
                    rValue = o.ObjDDL;
                }
            }
            return rValue;
        }
    }
}
