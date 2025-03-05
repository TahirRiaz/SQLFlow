using System;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using SQLFlowCore.Common;
using SQLFlowCore.Lineage;
using System.Text.Json;

namespace SQLFlowCore.Pipeline
{
    /// <summary>
    /// The ExecLineageLookup class is responsible for executing lineage lookups in SQLFlow.
    /// </summary>
    /// <remarks>
    /// This class provides a static method, Exec, which executes a lineage lookup based on the provided parameters.
    /// It uses the ConStringParser class to parse the connection string and then establishes a connection to the SQL server.
    /// Depending on the direction parameter (Dir), it either creates a LineageDescendants or LineageAncestors object and gets the result in SVG format.
    /// </remarks>
    public class ExecLineageLookup
    {
        private static string _execResult = "";
        
        #region ExecLineageLookupJson
        /// <summary>
        /// Executes a lineage lookup and returns the result in JSON format suitable for D3 visualization.
        /// </summary>
        /// <param name="sqlFlowConString">The connection string to the SQLFlow database.</param>
        /// <param name="FlowID">The ID of the flow to lookup. Default is "0".</param>
        /// <param name="allDep">A boolean value indicating whether to include all dependencies in the lookup. Default is false.</param>
        /// <param name="allBatches">A boolean value indicating whether to include all batches in the lookup. Default is false.</param>
        /// <param name="Dir">The direction of the lookup. "A" for ancestors and any other value for descendants. Default is "A".</param>
        /// <returns>A string representing the result of the lineage lookup in JSON format.</returns>
        public static string ExecJson(string sqlFlowConString, string FlowID = "0", bool allDep = false, bool allBatches = false, string Dir = "A")
        {
            var result = "false";
            _execResult = "";

            ConStringParser conStringParser = new ConStringParser(sqlFlowConString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            sqlFlowConString = conStringParser.ConBuilderMsSql.ConnectionString;

            var totalTime = new Stopwatch();
            new Stopwatch();
            totalTime.Start();
            using (var sqlFlowCon = new SqlConnection(sqlFlowConString))
            {
                try
                {
                    if (Dir == "A")
                    {
                        LineageDescendants dfs = new LineageDescendants(sqlFlowCon, int.Parse(FlowID), allDep, allBatches);
                        _execResult = dfs.GetResultJson();
                    }
                    else
                    {
                        LineageAncestors dfs = new LineageAncestors(sqlFlowCon, int.Parse(FlowID), allDep, allBatches);
                        _execResult = dfs.GetResultJson();
                    }

                    sqlFlowCon.Close();
                    sqlFlowCon.Dispose();
                }
                catch (Exception e)
                {
                    // Create a JSON error response
                    var errorObj = new
                    {
                        error = true,
                        message = e.Message,
                        stackTrace = e.StackTrace
                    };
                    _execResult = JsonSerializer.Serialize(errorObj, new JsonSerializerOptions { WriteIndented = true });
                }

                totalTime.Stop();

                result = _execResult;
            }

            return result;
        }
        #endregion ExecLineageLookupJson
    }
}
    
