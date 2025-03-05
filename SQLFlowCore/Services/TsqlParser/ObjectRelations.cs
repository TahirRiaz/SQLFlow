using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Xml;
using Newtonsoft.Json;
using System;
using Microsoft.SqlServer.Management.Smo;
using System.Linq;
using System.Text.Json.Nodes;

namespace SQLFlowCore.Services.TsqlParser
{
    /// <summary>
    /// Represents a class for managing and parsing object relations in SQL.
    /// </summary>
    /// <remarks>
    /// The ObjectRelations class provides functionality to parse different types of SQL objects such as Stored Procedures, Views, and User Defined Functions. 
    /// It generates commands for these objects and retrieves their execution plans. The class also handles the conversion of these plans into a list of 
    /// RelationReference objects, which are then serialized into a JSON string.
    /// </remarks>
    internal class ObjectRelations
    {
        /// <summary>
        /// Parses the object relations for a given SQL object.
        /// </summary>
        /// <param name="connectionString">The connection string to the SQL server.</param>
        /// <param name="objectType">The type of the SQL object (e.g., "StoredProcedure", "View", "UserDefinedFunction").</param>
        /// <param name="parsedObjectName">The name of the SQL object to parse.</param>
        /// <param name="Object">The SQL object to parse.</param>
        /// <returns>A JSON string representing the parsed object relations.</returns>
        /// <exception cref="Exception">Throws an exception if an error occurs during parsing.</exception>
        internal static string Parse(string connectionString, string objectType, string parsedObjectName, object Object, string ObjectCode)
        {
            string rValue = "";

            string planCmd = "";
            if (objectType == "StoredProcedure")
            {
                var srcObject = (StoredProcedure)Object;
                planCmd = CreateSpCommand(parsedObjectName, srcObject.Parameters);
            }
            else if (objectType == "View")
            {
                planCmd = CreateViewCommand(parsedObjectName);
            }
            else if (objectType == "Sql")
            {
                planCmd = ObjectCode;
            }
            else if (objectType == "UserDefinedFunction")
            {
                var srcObject = (UserDefinedFunction)Object;
                bool isTvf = srcObject.Columns?.Count > 0 ? true : false;
                planCmd = CreateUdfCommand(parsedObjectName, srcObject.Parameters, isTvf);
            }

            try
            {
                string executionPlan = GetExecutionPlan(connectionString, planCmd);
                if (executionPlan.Length > 0)
                {
                    XmlNodeList finalNodes = ParseExecutionPlan(executionPlan);
                    List<RelationReference> compareNodes = ConvertToCompareNodes(finalNodes, parsedObjectName);

                    compareNodes.RemoveAll(relationReference => string.IsNullOrEmpty(relationReference.Database));

                    if (compareNodes.Count > 0)
                    {
                        rValue = JsonConvert.SerializeObject(compareNodes, Newtonsoft.Json.Formatting.Indented);
                    }
                }
                else
                {
                    rValue = "";
                }
            }
            catch (Exception)
            {
                //return ex.Message;
                //throw new Exception($"Error parsing object relations: {ex.Message}");
                //return rValue
            }

            return rValue;
        }

        /// <summary>
        /// Creates a command for a User-Defined Function (UDF).
        /// </summary>
        /// <param name="udfName">The name of the UDF.</param>
        /// <param name="parameters">The parameters of the UDF.</param>
        /// <param name="isTvf">Indicates whether the UDF is a Table-Valued Function (TVF).</param>
        /// <returns>A string representing the command for the UDF.</returns>
        /// <remarks>
        /// If the UDF is a TVF, the command will be a SELECT statement from the UDF. 
        /// If the UDF is a scalar function, the command will be a SELECT statement with the UDF as a column.
        /// </remarks>
        private static string CreateUdfCommand(string udfName, UserDefinedFunctionParameterCollection parameters, bool isTvf)
        {
            StringBuilder command = new StringBuilder();

            if (isTvf)
            {
                // TVF is used like a table in a SELECT statement
                command.Append("SELECT * FROM ");
                command.Append(udfName);
                command.Append("(");
                AppendParameters(command, parameters);
                command.Append(")");
            }
            else
            {
                // Scalar function is used as a column
                command.Append("SELECT ");
                command.Append(udfName);
                command.Append("(");
                AppendParameters(command, parameters);
                command.Append(")");
            }

            return command.ToString();
        }

        /// <summary>
        /// Creates a SQL SELECT command for a given view.
        /// </summary>
        /// <param name="viewName">The name of the view.</param>
        /// <returns>A string representing a SQL SELECT command.</returns>
        /// <remarks>
        /// This method generates a SQL SELECT command that fetches all records from the specified view. 
        /// The WHERE clause in the command is designed to prevent the execution of the command.
        /// </remarks>
        private static string CreateViewCommand(string viewName)
        {
            StringBuilder command = new StringBuilder();
            command.Append("SELECT * FROM ");
            command.Append(viewName);
            //command.Append("WHERE 1 <> 2");

            return command.ToString();
        }

        /// <summary>
        /// Creates a command for a Stored Procedure (SP).
        /// </summary>
        /// <param name="spName">The name of the SP.</param>
        /// <param name="parameters">The parameters of the SP.</param>
        /// <returns>A string representing the command for the SP.</returns>
        /// <remarks>
        /// The command is created in the format "EXEC spName param1 = defaultValue1, param2 = defaultValue2, ...".
        /// The default values are generated based on the data types of the parameters.
        /// </remarks>
        internal static string CreateSpCommand(string spName, StoredProcedureParameterCollection parameters)
        {
            StringBuilder command = new StringBuilder();
            command.Append("EXEC ");
            command.Append(spName);
            command.Append(" ");

            for (int i = 0; i < parameters?.Count; i++)
            {
                command.Append(parameters[i].Name);
                command.Append(" = ");
                command.Append(GetDefaultValue(parameters[i].DataType));

                if (i < parameters.Count - 1)
                {
                    command.Append(", ");
                }
            }

            return command.ToString();
        }

        /// <summary>
        /// Appends the default values of the parameters to the given command.
        /// </summary>
        /// <param name="command">The StringBuilder instance to which the parameters will be appended.</param>
        /// <param name="parameters">The collection of parameters whose default values will be appended to the command.</param>
        /// <remarks>
        /// This method iterates through the provided UserDefinedFunctionParameterCollection and appends the default value of each parameter to the provided StringBuilder instance. 
        /// Parameters are separated by a comma.
        /// </remarks>
        private static void AppendParameters(StringBuilder command, UserDefinedFunctionParameterCollection parameters)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                command.Append(GetDefaultValue(parameters[i].DataType));
                if (i < parameters.Count - 1)
                    command.Append(", ");
            }
        }

        /// <summary>
        /// Returns the default value for a given SQL data type.
        /// </summary>
        /// <param name="dataType">The SQL data type for which to get the default value.</param>
        /// <returns>A string representation of the default value for the specified data type.</returns>
        /// <remarks>
        /// This method handles a wide range of SQL data types and provides a default value for each. For binary data types, SQL_VARIANT, user-defined data types, and any unknown types, it returns NULL. 
        /// </remarks>
        private static string GetDefaultValue(DataType dataType)
        {
            switch (dataType.SqlDataType)
            {
                case SqlDataType.Bit:
                    return "false";
                case SqlDataType.TinyInt:
                    return "0";
                case SqlDataType.SmallInt:
                    return "0";
                case SqlDataType.Int:
                    return "0";
                case SqlDataType.BigInt:
                    return "0";
                case SqlDataType.Numeric:
                case SqlDataType.Decimal:
                    return "0.0";
                case SqlDataType.Money:
                case SqlDataType.SmallMoney:
                    return "0.0";
                case SqlDataType.Float:
                    return "0.0";
                case SqlDataType.Real:
                    return "0.0";
                case SqlDataType.Date:
                case SqlDataType.DateTime:
                case SqlDataType.SmallDateTime:
                case SqlDataType.DateTime2:
                    return "'1900-01-01'";
                case SqlDataType.DateTimeOffset:
                    return "'1900-01-01T00:00:00+00:00'";
                case SqlDataType.Time:
                    return "'00:00:00'";
                case SqlDataType.Char:
                case SqlDataType.VarChar:
                case SqlDataType.Text:
                    return "''";
                case SqlDataType.NChar:
                case SqlDataType.NVarChar:
                case SqlDataType.NText:
                    return "N''";
                case SqlDataType.Binary:
                case SqlDataType.VarBinary:
                case SqlDataType.Image:
                    return "NULL"; // Binary data types typically don't have meaningful defaults
                case SqlDataType.Variant:
                    return "NULL"; // SQL_VARIANT can be used for any data type
                case SqlDataType.Xml:
                    return "'<root></root>'"; // Basic XML structure
                case SqlDataType.UniqueIdentifier:
                    return "'00000000-0000-0000-0000-000000000000'";
                case SqlDataType.UserDefinedDataType:
                    return "NULL"; // Default value for UDTs can be complex, so returning NULL for simplicity
                case SqlDataType.UserDefinedType:
                    return "NULL"; // Default for UDTs
                case SqlDataType.UserDefinedTableType:
                    return "NULL"; // Default for UDTTs
                case SqlDataType.None:
                default:
                    return "NULL"; // Fallback for any unknown types or None
            }
        }

        /// <summary>
        /// Converts a list of XML nodes into a list of RelationReference objects.
        /// </summary>
        /// <param name="xmlNodes">The XML nodes to be converted.</param>
        /// <param name="parsedObjectName">The name of the parsed object.</param>
        /// <returns>A list of RelationReference objects that represent the relationships in the parsed object.</returns>
        /// <remarks>
        /// This method iterates over the provided XML nodes, extracting information about the relationships in the parsed SQL object.
        /// Each relationship is represented as a RelationReference object, which includes details such as the database, schema, table, alias, and column involved in the relationship.
        /// The method also assigns a unique counter to each relationship for tracking purposes.
        /// </remarks>
        static List<RelationReference> ConvertToCompareNodes(XmlNodeList xmlNodes, string parsedObjectName)
        {
            var compareNodes = new List<RelationReference>();
            int relationCounter = 0;
            foreach (XmlNode node in xmlNodes)
            {
                if (node.OwnerDocument == null) continue;
                XmlNamespaceManager nsmgr = new XmlNamespaceManager(node.OwnerDocument.NameTable);
                nsmgr.AddNamespace("sm", "http://schemas.microsoft.com/sqlserver/2004/07/showplan");

                // Select the ColumnReference nodes correctly
                var colRefNodes = node.SelectNodes(".//sm:ScalarOperator/sm:Identifier/sm:ColumnReference", nsmgr);

                foreach (XmlNode colNode in colRefNodes)
                {
                    var colRef = new RelationReference
                    {
                        ParsedObjectName = parsedObjectName,
                        RelationshipCounter = relationCounter,
                        CompareOp = node.Attributes["CompareOp"].Value,
                        Database = GetAttributeValue(colNode, "Database"),
                        Schema = GetAttributeValue(colNode, "Schema"),
                        Table = GetAttributeValue(colNode, "Table"),
                        Alias = GetAttributeValue(colNode, "Alias"),
                        Column = GetAttributeValue(colNode, "Column")
                    };
                    compareNodes.Add(colRef);
                }

                relationCounter++;
            }

            return compareNodes;
        }

        /// <summary>
        /// Retrieves the value of a specified attribute from a given XML node.
        /// </summary>
        /// <param name="node">The XML node from which to retrieve the attribute value.</param>
        /// <param name="attributeName">The name of the attribute whose value is to be retrieved.</param>
        /// <returns>The value of the specified attribute. If the attribute does not exist, returns an empty string.</returns>
        static string GetAttributeValue(XmlNode node, string attributeName)
        {
            return node.Attributes[attributeName]?.Value ?? string.Empty;
        }

        /// <summary>
        /// Parses the provided execution plan XML and filters the nodes based on specific requirements.
        /// </summary>
        /// <param name="executionPlanXml">The XML string of the execution plan to parse.</param>
        /// <returns>A list of XML nodes that meet the specific requirements for comparison.</returns>
        /// <remarks>
        /// This method loads the execution plan XML into an XmlDocument, sets up a namespace manager for the SQL Server showplan namespace, and selects nodes based on an XPath expression. 
        /// The selected nodes are then filtered further by the FilterCompareNodes method.
        /// </remarks>
        static XmlNodeList ParseExecutionPlan(string executionPlanXml)
        {
            XmlNodeList finalNodes = null;

            XmlDocument planXml = new XmlDocument();
            planXml.LoadXml(executionPlanXml.ToString());

            XmlNamespaceManager nsmgr = new XmlNamespaceManager(planXml.NameTable);
            nsmgr.AddNamespace("sm", "http://schemas.microsoft.com/sqlserver/2004/07/showplan");

            // Update the XPath expression to your specific requirements
            string xpath = "//sm:Compare[@CompareOp]";
            finalNodes = planXml.SelectNodes(xpath, nsmgr);

            XmlNodeList compareNodes = planXml.SelectNodes("//sm:Compare", nsmgr);

            return FilterCompareNodes(compareNodes, nsmgr);
        }

        /// <summary>
        /// Filters the provided Compare nodes based on specific criteria.
        /// </summary>
        /// <param name="compareNodes">The XmlNodeList containing Compare nodes to be filtered.</param>
        /// <param name="nsmgr">The XmlNamespaceManager used for selecting nodes in the XmlDocument.</param>
        /// <returns>Returns a XmlNodeList containing the filtered Compare nodes.</returns>
        /// <remarks>
        /// This method filters the provided Compare nodes based on the following criteria:
        /// - The Compare node must have a CompareOp attribute.
        /// - The Compare node must have at least two ScalarOperator nodes.
        /// - Each ScalarOperator node must have at least one ColumnReference node.
        /// - Each ColumnReference node must have a Database attribute and its Schema attribute value must not be "[sys]".
        /// </remarks>
        static XmlNodeList FilterCompareNodes(XmlNodeList compareNodes, XmlNamespaceManager nsmgr)
        {
            XmlDocument filteredXml = new XmlDocument();
            XmlNode root = filteredXml.CreateElement("root");
            filteredXml.AppendChild(root);

            foreach (XmlNode compareNode in compareNodes)
            {
                // Ensure the Compare node has a CompareOp attribute
                if (compareNode.Attributes["CompareOp"] == null)
                    continue;

                // CheckForError for at least two ScalarOperator nodes
                var scalarOperators = compareNode.SelectNodes(".//sm:ScalarOperator", nsmgr);
                if (scalarOperators == null || scalarOperators.Count < 2)
                    continue;

                // CheckForError each ScalarOperator node for at least one ColumnReference node
                bool allScalarOperatorsValid = true;
                foreach (XmlNode scalarOperator in scalarOperators)
                {
                    var columnReferences = scalarOperator.SelectNodes(".//sm:ColumnReference", nsmgr);
                    if (columnReferences == null || columnReferences.Count < 1)
                    {
                        allScalarOperatorsValid = false;
                        break;
                    }

                    // Ensure each ColumnReference has a Database attribute
                    bool allColumnReferencesHaveDatabase = columnReferences
                        .Cast<XmlNode>()
                        .All(cr => cr.Attributes["Database"] != null && cr.Attributes["Schema"].Value != "[sys]");

                    if (!allColumnReferencesHaveDatabase)
                    {
                        allScalarOperatorsValid = false;
                        break;
                    }
                }

                if (!allScalarOperatorsValid)
                    continue;


                // Add the node to the result if it meets all the criteria
                XmlNode importedNode = filteredXml.ImportNode(compareNode, true);
                root.AppendChild(importedNode);
            }

            return root.ChildNodes;
        }

        /// <summary>
        /// Retrieves the execution plan of a SQL query.
        /// </summary>
        /// <param name="connectionString">The connection string to the SQL Server database.</param>
        /// <param name="query">The SQL query for which the execution plan is to be retrieved.</param>
        /// <returns>A string representation of the execution plan in XML format.</returns>
        /// <remarks>
        /// This method connects to the SQL Server database using the provided connection string, 
        /// enables the SHOWPLAN_XML setting, executes the provided query, and retrieves the execution plan.
        /// After retrieving the plan, it disables the SHOWPLAN_XML setting and closes the connection.
        /// </remarks>
        static string GetExecutionPlan(string connectionString, string query)
        {
            StringBuilder executionPlan = new StringBuilder();


            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                try
                {
                    // First, set SHOWPLAN_XML ON
                    using (SqlCommand commandShowPlan = new SqlCommand("SET SHOWPLAN_XML ON;", connection))
                    {
                        commandShowPlan.ExecuteNonQuery();
                    }

                    // Then, execute the query to get the plan
                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        using (SqlDataReader reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    executionPlan.AppendLine(reader.GetValue(i).ToString());
                                }
                            }
                        }
                    }

                    // Optionally, reset SHOWPLAN_XML to OFF
                    using (SqlCommand commandShowPlanOff = new SqlCommand("SET SHOWPLAN_XML OFF;", connection))
                    {
                        commandShowPlanOff.ExecuteNonQuery();
                    }
                }
                catch (Exception)
                {

                }
            }

            return executionPlan.ToString();
        }
    }


    public class JsonRelationCombiner
    {
        public static List<RelationReference> CombineJsonStrings(string json1, string json2)
        {
            // Deserialize the JSON strings into lists of objects
            var list1 = JsonConvert.DeserializeObject<List<RelationReference>>(json1);
            var list2 = JsonConvert.DeserializeObject<List<RelationReference>>(json2);

            // Combine the two lists
            var combinedList = new List<RelationReference>();
            if (list1 != null)
            {
                combinedList.AddRange(list1);
            }
            if (list2 != null)
            {
                combinedList.AddRange(list2);
            }

            return combinedList;
        }

        // Optionally, convert the combined list back to a JSON string
        public static string ConvertToJson(List<JsonObject> combinedList)
        {
            return JsonConvert.SerializeObject(combinedList, Newtonsoft.Json.Formatting.Indented);
        }
    }

    /// <summary>
    /// Represents a relationship in a parsed SQL object.
    /// </summary>
    /// <remarks>
    /// This class is used to store information about a relationship in a parsed SQL object, 
    /// including the database, schema, table, alias, and column involved in the relationship.
    /// It also stores the name of the parsed object and a counter for tracking the number of relationships.
    /// </remarks>
    public class RelationReference
    {
        /// <summary>
        /// Gets or sets the name of the parsed SQL object.
        /// </summary>
        public string ParsedObjectName { get; set; }
        /// <summary>
        /// Gets or sets the counter for tracking the number of relationships.
        /// </summary>
        public int RelationshipCounter { get; set; }
        /// <summary>
        /// Gets or sets the comparison operator used in the relationship.
        /// </summary>
        public string CompareOp { get; set; }
        /// <summary>
        /// Gets or sets the name of the database involved in the relationship.
        /// </summary>
        public string Database { get; set; }
        /// <summary>
        /// Gets or sets the name of the schema involved in the relationship.
        /// </summary>
        public string Schema { get; set; }
        /// <summary>
        /// Gets or sets the name of the table involved in the relationship.
        /// </summary>
        public string Table { get; set; }
        /// <summary>
        /// Gets or sets the alias used in the relationship.
        /// </summary>
        public string Alias { get; set; }
        /// <summary>
        /// Gets or sets the name of the column involved in the relationship.
        /// </summary>
        public string Column { get; set; }
    }
}
