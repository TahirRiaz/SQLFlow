using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using SQLFlowCore.Common;
using System.Text.Json;
using static SQLFlowCore.Lineage.LineageDescendants;

namespace SQLFlowCore.Lineage
{
    public class LineageAncestors
    {
        private DataTable lineageTable;
        private Dictionary<int, List<int>> reverseGraph = new();
        private HashSet<int> visited = new();
        private List<int> result = new();
        private bool FetchAllAncestors = true;
        private int startNode = 0;
        private bool AllBatchs = false;
        private string BatchList = "";
        private string BaseAFUrl = "";
        private int flowID;
        public LineageAncestors(SqlConnection sqlFlowCon, int FlowID, bool fetchAllAncestors, bool allBatchs)
        {
            flowID = FlowID;
            string cmdSQL = "SELECT  [flw].[GetWebApiUrl]() baseURL";
            DataTable dt = CommonDB.GetData(sqlFlowCon, cmdSQL, 300);
            BaseAFUrl = dt.Rows[0]["baseURL"]?.ToString() ?? string.Empty;

            DataTable lineageMapBase = CommonDB.GetData(sqlFlowCon, "[flw].[GetLineageMapObjects]", 300);
            lineageTable = lineageMapBase;
            FetchAllAncestors = fetchAllAncestors;
            AllBatchs = allBatchs;
            startNode = GetToObjectMKByFlowID(lineageTable, flowID);
            BuildReverseGraph();
            Execute();

        }

        internal List<int> GetObjectMKList()
        {
            return result;
        }

        public DataTable GetResult()
        {
            DataTable baseTbl = LineageHelper.GetRowsByFromObjectMKList(lineageTable, result);
            LineageParser lineage = new LineageParser(baseTbl);
            DataTable lineageResult = lineage.GetResult();

            if (AllBatchs == false)
            {
                lineageResult = FilterByBatch(lineageResult);
            }

            HashSet<int> visit = new HashSet<int>();
            List<int> res = new List<int>();

            foreach (DataRow row in lineageResult.Rows)
            {
                if (row["Status"].ToString() == "0")
                {
                    int toObjectMK = Convert.ToInt32(row["ToObjectMK"]);
                    if (visit.Contains(toObjectMK) == false)
                    {
                        res = DfsDescendants(toObjectMK, res, visit);
                        UpdateDataStatus(lineageResult, res);
                    }
                }
            }

            lineageResult.DefaultView.Sort = "Step ASC";
            DataTable sortedTable = lineageResult.DefaultView.ToTable();

            return sortedTable;
        }

        internal DataTable FilterByBatch(DataTable baseTable)
        {
            DataTable filteredTable = lineageTable.Clone();  // Clone structure

            if (string.IsNullOrEmpty(BatchList))
            {
                return baseTable.Copy(); // Return a copy to ensure the original DataTable is not accidentally modified
            }

            List<string> batchList = BatchList.Split(',').Select(b => b.Trim().ToUpper()).ToList();

            foreach (DataRow row in baseTable.Rows)
            {
                if (batchList.Contains(row["Batch"].ToString().ToUpper()))
                {
                    filteredTable.ImportRow(row);
                }
            }

            filteredTable.DefaultView.Sort = "Step ASC";
            DataTable sortedTable = filteredTable.DefaultView.ToTable();

            return sortedTable;
        }

        /// <summary>
        /// Returns the lineage data in JSON format suitable for D3 visualization.
        /// </summary>
        /// <returns>A JSON string representing the lineage graph with nodes and edges.</returns>
        public string GetResultJson()
        {
            DataTable baseTbl = GetResult();

            // First calculate all node levels and colors using the comprehensive method
            Dictionary<int, LineageNodeData> nodeData = CalculateNodeLevels(baseTbl);

            var nodes = new List<object>();
            var edges = new List<object>();
            var nodesSet = new HashSet<string>(); // To track unique nodes

            foreach (DataRow dr in baseTbl.Rows)
            {
                int flowID = (int)dr["FlowID"];
                int fromObjectMK = (int)dr["FromObjectMK"];
                int toObjectMK = (int)dr["ToObjectMK"];

                // Get data for both nodes from nodeData dictionary
                var fromNodeData = nodeData[fromObjectMK];
                var toNodeData = nodeData[toObjectMK];

                bool DeactivateFromBatch = (dr["DeactivateFromBatch"]?.ToString() ?? string.Empty).Equals("True");
                bool DataStatus = (dr["DataStatus"]?.ToString() ?? string.Empty).Equals("True");

                // Status symbols for consistency with SVG output
                string StatusSymbol = "✅";
                if (fromNodeData.Status == 0)
                {
                    StatusSymbol = "❌";
                }
                else if (fromNodeData.Status == -1)
                {
                    StatusSymbol = "❓";
                }

                string fromObjShort = StatusSymbol + " " + LineageHelper.ShortenObjectName(fromNodeData.ObjectName);

                // For to-node, use its own status
                StatusSymbol = "✅";
                if (toNodeData.Status == 0)
                {
                    StatusSymbol = "❌";
                }
                else if (toNodeData.Status == -1)
                {
                    StatusSymbol = "❓";
                }

                string toObjShort = StatusSymbol + " " + LineageHelper.ShortenObjectName(toNodeData.ObjectName);

                // Create tooltips using the consistent node data
                string fromToolTip = $@"FromObject: {fromNodeData.ObjectName}
Batch: {fromNodeData.Batch}
SysAlias: {fromNodeData.SysAlias}
LatestFileProcessed: {fromNodeData.LatestFileProcessed}
LastExec: {fromNodeData.LastExec}
MK: {fromObjectMK}
recID: {dr["RecID"].ToString()}
";

                string toToolTip = $@"ToObject: {toNodeData.ObjectName}
Batch: {toNodeData.Batch}
SysAlias: {toNodeData.SysAlias}
LatestFileProcessed: {toNodeData.LatestFileProcessed}
LastExec: {toNodeData.LastExec}
MK: {toObjectMK}
recID: {dr["RecID"].ToString()}
";

                // Add from node if not already added
                string fromNodeId = fromObjectMK.ToString();
                if (!nodesSet.Contains(fromNodeId))
                {
                    nodesSet.Add(fromNodeId);
                    nodes.Add(new
                    {
                        id = int.Parse(fromNodeId),
                        type = fromNodeData.FlowType,
                        name = fromObjShort,  // Using name instead of label to match original
                        color = fromNodeData.ColorHex,
                        description = $"flw:{fromNodeData.FlowID}, type:{fromNodeData.FlowType}, batch: {fromNodeData.Batch}, step:{fromNodeData.Level * 100}",
                        url = $"/search/{fromNodeData.FlowID}",
                        datetime = $"LEDT: {fromNodeData.LastExec}",
                        level = fromNodeData.Level,
                        tooltip = fromToolTip,
                        dataStatus = fromNodeData.DataStatus,
                        status = fromNodeData.Status,
                        fullName = fromNodeData.ObjectName,
                        batch = fromNodeData.Batch
                    });
                }

                // Add to node if not already added
                string toNodeId = toObjectMK.ToString();
                if (!nodesSet.Contains(toNodeId))
                {
                    nodesSet.Add(toNodeId);
                    nodes.Add(new
                    {
                        id = int.Parse(toNodeId),  // Using int.Parse to match the original
                        type = toNodeData.FlowType,
                        name = toObjShort,  // Using name instead of label to match original
                        color = toNodeData.ColorHex,
                        description = $"flw:{toNodeData.FlowID}, type:{toNodeData.FlowType}, batch:{toNodeData.Batch}, step:{toNodeData.Level * 100}",
                        url = $"/search/{toNodeData.FlowID}",
                        datetime = $"LEDT: {toNodeData.LastExec}",
                        level = toNodeData.Level,
                        tooltip = toToolTip,
                        dataStatus = toNodeData.DataStatus,
                        status = toNodeData.Status,
                        fullName = toNodeData.ObjectName,
                        batch = toNodeData.Batch
                    });
                }

                // Determine edge style
                string edgeStyle;
                string sourceArrowStyle = "none";
                string targetArrowStyle;

                if (dr["SolidEdge"].ToString() == "0")
                {
                    edgeStyle = "dashed";
                    targetArrowStyle = "none";
                }
                else
                {
                    edgeStyle = DeactivateFromBatch ? "dotted" : "solid";
                    targetArrowStyle = "default";
                }

                // Use consistent edge properties from node data
                edges.Add(new
                {
                    source = int.Parse(fromNodeId),  // Using int.Parse to match the original
                    target = int.Parse(toNodeId),    // Using int.Parse to match the original
                    label = "",
                    style = edgeStyle,
                    width = 3,
                    color = fromNodeData.ColorHex, // Use source node's color for edge
                    sourceArrowStyle = sourceArrowStyle,
                    targetArrowStyle = targetArrowStyle,
                    level = fromNodeData.Level,
                    flowID = flowID
                });
            }

            var result = new
            {
                nodes = nodes,
                edges = edges
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }

        

        

        internal void Execute()
        {
            // Start DFS from the child node
            Dfs(startNode);

            DataTable batchTbl = LineageHelper.GetRowsByFromObjectMKList(lineageTable, result);
            BatchList = LineageHelper.CombineBatchValues(batchTbl);

            if (FetchAllAncestors)
            {
                foreach (int node in result)
                {
                    Dfs(node);
                }
            }
        }

        private void BuildReverseGraph()
        {
            foreach (DataRow row in lineageTable.Rows)
            {
                int fromObject = (int)row["FromObjectMK"];
                int toObject = (int)row["ToObjectMK"];

                if (!reverseGraph.ContainsKey(toObject))
                {
                    reverseGraph[toObject] = new List<int>();
                }

                reverseGraph[toObject].Add(fromObject);
            }
        }



        private void Dfs(int startNode)
        {
            if (visited.Contains(startNode)) return;

            visited.Add(startNode);
            result.Add(startNode);

            if (reverseGraph.ContainsKey(startNode))
            {
                foreach (int parent in reverseGraph[startNode])
                {
                    Dfs(parent);
                }
            }
        }


        private List<int> DfsDescendants(int startNode, List<int> res, HashSet<int> visit)
        {
            if (visit.Contains(startNode) == false)
            {
                visit.Add(startNode);
                res.Add(startNode);

                if (reverseGraph.ContainsKey(startNode))
                {
                    foreach (int parent in reverseGraph[startNode])
                    {
                        DfsDescendants(parent, res, visit);
                    }
                }
            }

            return res;
        }

        private void UpdateDataStatus(DataTable table, List<int> values)
        {
            foreach (DataRow row in table.Rows)
            {
                int fromObjectMK = Convert.ToInt32(row["FromObjectMK"]);
                int toObjectMK = Convert.ToInt32(row["ToObjectMK"]);
                if (values.Contains(fromObjectMK) || values.Contains(toObjectMK))
                {
                    row["DataStatus"] = 0;
                }
            }
        }



        internal static int GetToObjectMKByFlowID(DataTable table, int flowID)
        {
            foreach (DataRow row in table.Rows)
            {
                int flowIDInt = Convert.ToInt32(flowID);
                if (row["FlowID"].Equals(flowIDInt))
                {
                    return Convert.ToInt32(row["ToObjectMK"]);
                }
            }
            return 0; // Return null if FlowID not found
        }
    }
}
