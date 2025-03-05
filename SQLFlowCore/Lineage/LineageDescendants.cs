using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Runtime.InteropServices;
using SQLFlowCore.Common;
using System.Text.Json;

namespace SQLFlowCore.Lineage
{
    /// <summary>
    /// Class for parsing lineage information in order to identify and retrieve all descendants
    /// (and optionally dependencies) of a particular FlowID in a lineage graph.
    /// </summary>
    public class LineageDescendants
    {
        /// <summary>
        /// The set of start nodes (ObjectMKs) that belong to the primary FlowID of interest.
        /// </summary>
        private List<int> _startNodes = new List<int>();

        /// <summary>
        /// The underlying DataTable containing all lineage edges (FromObjectMK -> ToObjectMK).
        /// </summary>
        private DataTable _lineageTable = new("LineageEdge");

        /// <summary>
        /// The adjacency list for the lineage graph: a mapping of node -> list of outbound neighbor nodes.
        /// </summary>
        private Dictionary<int, List<int>> _graph = new();

        /// <summary>
        /// The set of nodes we have already visited in DFS.
        /// </summary>
        private HashSet<int> _visited = new();

        /// <summary>
        /// The final list of nodes in the result set (descendants and possibly dependencies).
        /// </summary>
        private List<int> _result = new();

        /// <summary>
        /// Whether to include all dependencies (true) or a restricted subset (false).
        /// </summary>
        private bool _allDependencies = true;

        /// <summary>
        /// The FlowID we are focusing on.
        /// </summary>
        private int _flowID = 0;

        /// <summary>
        /// Whether to include all batches or restrict by the discovered/combined batch list.
        /// </summary>
        private bool _allBatches = false;

        /// <summary>
        /// A comma-separated list of batches discovered from the lineage relevant to <see cref="_flowID"/>.
        /// Used when <see cref="_allBatches"/> is false.
        /// </summary>
        private string _batchList = "";

        #region Constructors

        public LineageDescendants(SqlConnection sqlFlowCon, int flowID, bool allDep, bool allBatches)
        {
            // Get the base lineage map from the DB
            DataTable lineageMapBase = CommonDB.GetData(sqlFlowCon, "[flw].[GetLineageMapObjects]", 300);

            _lineageTable = lineageMapBase;
            _allDependencies = allDep;
            _flowID = flowID;
            _allBatches = allBatches;

            // Identify the "start nodes" from this FlowID
            _startNodes = GetFromObjectMKsByFlowID(_lineageTable, _flowID);

            // Build the graph adjacency list
            BuildGraph();

            // Execute the core logic to fill up _result
            Execute();
        }

        public LineageDescendants(DataTable lineageMapBase, int flowID, bool allDep, bool allBatches)
        {
            _lineageTable = lineageMapBase;
            _allDependencies = allDep;
            _flowID = flowID;
            _allBatches = allBatches;

            // Identify the "start nodes" from this FlowID
            _startNodes = GetFromObjectMKsByFlowID(_lineageTable, _flowID);

            // Build the graph adjacency list
            BuildGraph();

            // Execute the core logic to fill up _result
            Execute();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns the final list of ObjectMKs discovered (descendants + any dependencies).
        /// </summary>
        /// <returns>List of integer ObjectMKs in the result set.</returns>
        public List<int> GetObjectMKList()
        {
            return _result;
        }

        /// <summary>
        /// Returns the final lineage DataTable, filtered and sorted by the chosen rules.
        /// </summary>
        /// <returns>DataTable of relevant lineage rows, sorted by Step ascending.</returns>
        public DataTable GetResult()
        {
            // Filter the lineage table by only those edges whose FromObjectMK is in the final result
            DataTable baseTbl = GetRowsByFromObjectMKList(_lineageTable, _result);

            // Parse that subset with the lineage parser
            LineageParser lineageParser = new LineageParser(baseTbl);
            DataTable lineageResult = lineageParser.GetResult();

            // Filter out rows whose Level is less than the level of the main FlowID
            // so that we keep everything at or "below" the main Flow
            // DataTable filteredResult = GetRowsWithLevelGreaterOrEqualToFlowID(lineageResult, _flowID);

            DataTable filteredResult = lineageResult;

            // If not including all batches, do a batch-based filter
            if (!_allBatches)
            {
                filteredResult = FilterByBatch(filteredResult);
            }

            // If any row has Status = 0, the data might be stale. We mark all its descendants' DataStatus=0
            // i.e., set them to false.
            HashSet<int> visitedDownstream = new HashSet<int>();
            List<int> resDownstream = new List<int>();

            foreach (DataRow row in filteredResult.Rows)
            {
                if (row["Status"].ToString() == "0")
                {
                    int toObjectMK = Convert.ToInt32(row["ToObjectMK"]);
                    if (!visitedDownstream.Contains(toObjectMK))
                    {
                        resDownstream = DfsDescendants(toObjectMK, resDownstream, visitedDownstream);
                        UpdateDataStatus(filteredResult, resDownstream);
                    }
                }
            }

            // Finally, sort by "Step" ascending
            filteredResult.DefaultView.Sort = "Step ASC";
            DataTable sortedTable = filteredResult.DefaultView.ToTable();
            return sortedTable;
        }

        /// <summary>
        /// Returns a JSON representation of the final result, including node/edge data, levels, colors, etc.
        /// </summary>
        /// <returns>String containing JSON for nodes and edges.</returns>
        public string GetResultJson()
        {
            DataTable baseTbl = GetResult();

            // First, calculate node levels & color data
            Dictionary<int, LineageNodeData> nodeData = CalculateNodeLevels(baseTbl);

            var nodes = new List<object>();
            var edges = new List<object>();
            var nodesSet = new HashSet<int>(); // track unique node IDs

            foreach (DataRow dr in baseTbl.Rows)
            {
                int flowID = (int)dr["FlowID"];
                string flowType = (string)dr["FlowType"];
                string batch = (string)dr["Batch"];
                int status = (int)dr["Status"];

                int fromObjectMK = (int)dr["FromObjectMK"];
                int toObjectMK = (int)dr["ToObjectMK"];

                // Use the pre-calculated metadata from nodeData
                var fromNodeData = nodeData[fromObjectMK];
                var toNodeData = nodeData[toObjectMK];

                bool deactivateFromBatch = (dr["DeactivateFromBatch"]?.ToString() ?? string.Empty)
                                           .Equals("True", StringComparison.OrdinalIgnoreCase);
                bool dataStatus = (dr["DataStatus"]?.ToString() ?? string.Empty)
                                  .Equals("True", StringComparison.OrdinalIgnoreCase);

                // Convert status to a symbol
                string statusSymbol = "✅";
                if (status == 0) statusSymbol = "❌";
                else if (status == -1) statusSymbol = "❓";

                // Use a short name for display
                string fromObjShort = statusSymbol + " " + LineageHelper.ShortenObjectName(dr["FromObject"]?.ToString() ?? "");
                string toObjShort = statusSymbol + " " + LineageHelper.ShortenObjectName(dr["ToObject"]?.ToString() ?? "");

                // Build tooltips
                string fromToolTip = $@"FromObject: {dr["FromObject"]}
Batch: {batch}
SysAlias: {dr["SysAlias"]}
LatestFileProcessed: {dr["LatestFileProcessed"]}
LastExec: {dr["LastExec"]}
MK: {dr["FromObjectMK"]}
recID: {dr["RecID"]}
";

                string toToolTip = $@"ToObject: {dr["ToObject"]}
Batch: {batch}
SysAlias: {dr["SysAlias"]}
LatestFileProcessed: {dr["LatestFileProcessed"]}
LastExec: {dr["LastExec"]}
MK: {dr["ToObjectMK"]}
recID: {dr["RecID"]}
";

                // Add from-node if not present
                if (!nodesSet.Contains(fromObjectMK))
                {
                    nodesSet.Add(fromObjectMK);
                    nodes.Add(new
                    {
                        id = fromObjectMK,
                        type = fromNodeData.FlowType,
                        name = fromObjShort,
                        color = fromNodeData.ColorHex,
                        description = $"flw:{fromNodeData.FlowID}, type:{fromNodeData.FlowType}, batch:{fromNodeData.Batch}, step:{fromNodeData.Level * 100}",
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

                // Add to-node if not present
                if (!nodesSet.Contains(toObjectMK))
                {
                    nodesSet.Add(toObjectMK);
                    nodes.Add(new
                    {
                        id = toObjectMK,
                        type = toNodeData.FlowType,
                        name = toObjShort,
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
                string edgeStyle = "solid";
                string sourceArrowStyle = "none";
                string targetArrowStyle = "default";

                if (dr["SolidEdge"]?.ToString() == "0")
                {
                    edgeStyle = "dashed";
                    targetArrowStyle = "none";
                }
                else if (deactivateFromBatch)
                {
                    edgeStyle = "dotted";
                }

                // Add an edge. We color it using the "fromNodeData"
                edges.Add(new
                {
                    source = fromObjectMK,
                    target = toObjectMK,
                    label = "",
                    style = edgeStyle,
                    width = 3,
                    color = fromNodeData.ColorHex,
                    sourceArrowStyle = sourceArrowStyle,
                    targetArrowStyle = targetArrowStyle,
                    level = fromNodeData.Level,  // store parent's level, if desired
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

        #endregion

        #region Core Execution

        /// <summary>
        /// Orchestrates the main logic: obtains a topological order of the graph,
        /// does DFS from the primary FlowID start-nodes, and optionally adds further dependencies
        /// according to <see cref="_allDependencies"/>.
        /// </summary>
        private void Execute()
        {
            // First get a topological ordering of the entire graph (or partial if cycles)
            List<int> topoOrder = TopologicalSortCycleSafe();

            // Perform DFS from each root node for the FlowID
            foreach (int startNode in _startNodes)
            {
                DfsCollect(startNode);
            }

            // Build an initial batch list from the discovered nodes
            DataTable batchTbl = GetRowsByFromObjectMKList(_lineageTable, _result);
            _batchList = LineageHelper.CombineBatchValues(batchTbl);

            // If user wants ALL dependencies
            if (_allDependencies)
            {
                // For each node in topological order, if it is a direct dependency of something in _result, include it.
                foreach (int node in topoOrder)
                {
                    if (IsDependencyForAny(node))
                    {
                        DfsCollect(node);
                    }
                }
            }
            //else
            //{
            //    // If user wants partial dependencies, check specifically for "skey" or "mkey" references
            //    foreach (int node in topoOrder)
            //    {
            //        if (IsSkeyDep(node))
            //        {
            //            DfsCollect(node);
            //        }
            //    }

            //    foreach (int node in topoOrder)
            //    {
            //        if (IsMkeyDep(node))
            //        {
            //            DfsCollect(node);
            //        }
            //    }
            //}
        }

        #endregion

        #region Filtering & Batch Logic

        /// <summary>
        /// Filters a <paramref name="baseTable"/> to keep only rows whose 'FromObjectMK' is in <paramref name="fromObjectMKList"/>.
        /// Then sorts by Step ascending.
        /// </summary>
        private static DataTable GetRowsByFromObjectMKList(DataTable baseTable, List<int> fromObjectMKList)
        {
            DataTable filtered = baseTable.Clone(); // same structure, no rows
            foreach (DataRow row in baseTable.Rows)
            {
                int fromObjectMK = Convert.ToInt32(row["FromObjectMK"]);
                if (fromObjectMKList.Contains(fromObjectMK))
                {
                    DataRow newRow = filtered.NewRow();
                    newRow.ItemArray = row.ItemArray;
                    filtered.Rows.Add(newRow);
                }
            }
            filtered.DefaultView.Sort = "Step ASC";
            return filtered.DefaultView.ToTable();
        }

        /// <summary>
        /// Filters rows in <paramref name="table"/> to only those with a FlowID matching <paramref name="flowID"/>,
        /// extracting their unique 'FromObjectMK'.
        /// </summary>
        internal static List<int> GetFromObjectMKsByFlowID(DataTable table, int flowID)
        {
            List<int> result = new();
            foreach (DataRow row in table.Rows)
            {
                if (row.Field<int>("FlowID") == flowID)
                {
                    result.Add(row.Field<int>("FromObjectMK"));
                }
            }
            return result;
        }

        /// <summary>
        /// Returns rows whose Level >= the Level of <paramref name="startFlowID"/>. 
        /// If the FlowID is not found in the table, returns an empty table.
        /// </summary>
        internal static DataTable GetRowsWithLevelGreaterOrEqualToFlowID(DataTable lineageTable, int startFlowID)
        {
            DataTable filtered = lineageTable.Clone();

            // Attempt to find the row that has the FlowID= startFlowID in the table, to get its "Level"
            int startLevel = -1;
            foreach (DataRow row in lineageTable.Rows)
            {
                if (row.Field<int>("FlowID") == startFlowID)
                {
                    startLevel = row.Field<int>("Level");
                    break;
                }
            }
            // If not found, return empty
            if (startLevel == -1)
                return filtered;

            // Keep rows with Level >= that "startLevel"
            foreach (DataRow row in lineageTable.Rows)
            {
                if (row.Field<int>("Level") >= startLevel)
                {
                    DataRow newRow = filtered.NewRow();
                    newRow.ItemArray = row.ItemArray.Clone() as object[];
                    filtered.Rows.Add(newRow);
                }
            }

            filtered.DefaultView.Sort = "Step ASC";
            return filtered.DefaultView.ToTable();
        }

        /// <summary>
        /// If <see cref="_batchList"/> is empty, returns a copy of <paramref name="baseTable"/>.
        /// Otherwise, keeps only rows whose 'Batch' is in the comma-separated list.
        /// </summary>
        internal DataTable FilterByBatch(DataTable baseTable)
        {
            DataTable filteredTable = _lineageTable.Clone();  // same structure

            if (string.IsNullOrEmpty(_batchList))
            {
                // Return a copy of the original
                return baseTable.Copy();
            }

            List<string> batchList = _batchList.Split(',')
                .Select(b => b.Trim().ToUpper())
                .Where(x => !string.IsNullOrEmpty(x))
                .ToList();

            foreach (DataRow row in baseTable.Rows)
            {
                string rowBatch = row["Batch"]?.ToString().ToUpper() ?? "";
                if (batchList.Contains(rowBatch))
                {
                    filteredTable.ImportRow(row);
                }
            }

            filteredTable.DefaultView.Sort = "Step ASC";
            return filteredTable.DefaultView.ToTable();
        }

        #endregion

        #region Topological Sort (Cycle Safe)

        /// <summary>
        /// Builds the adjacency list from the lineage table.
        /// Ensures every unique node (fromObject, toObject) appears in the dictionary,
        /// even if it has no outbound edges.
        /// </summary>
        private void BuildGraph()
        {
            _graph.Clear();

            // Collect a set of all distinct node IDs
            HashSet<int> allNodes = new HashSet<int>();

            foreach (DataRow row in _lineageTable.Rows)
            {
                int fromObject = row.Field<int>("FromObjectMK");
                int toObject = row.Field<int>("ToObjectMK");

                allNodes.Add(fromObject);
                allNodes.Add(toObject);
            }

            // Initialize adjacency list
            foreach (int node in allNodes)
            {
                if (!_graph.ContainsKey(node))
                {
                    _graph[node] = new List<int>();
                }
            }

            // Fill adjacency
            foreach (DataRow row in _lineageTable.Rows)
            {
                int fromObject = row.Field<int>("FromObjectMK");
                int toObject = row.Field<int>("ToObjectMK");
                _graph[fromObject].Add(toObject);
            }
        }

        /// <summary>
        /// Returns a topological ordering of the nodes if the graph is acyclic. 
        /// If cycles exist, it returns a partial order of the acyclic portion. 
        /// </summary>
        private List<int> TopologicalSortCycleSafe()
        {
            var visited = new Dictionary<int, int>(); // 0=unvisited, 1=visiting, 2=visited
            var stack = new Stack<int>();

            // For consistent ordering across multiple runs, you could .OrderBy(...) the keys
            foreach (int node in _graph.Keys)
            {
                visited[node] = 0;
            }

            foreach (int node in _graph.Keys)
            {
                if (visited[node] == 0)
                {
                    DfsTopo(node, visited, stack);
                }
            }

            return stack.Reverse().ToList();
        }

        /// <summary>
        /// Utility function for topological sort that also tolerates cycles by skipping
        /// cycle edges (i.e. doesn't push them onto the stack).
        /// </summary>
        private void DfsTopo(int current, Dictionary<int, int> visited, Stack<int> stack)
        {
            visited[current] = 1; // mark 'visiting'

            foreach (int neighbor in _graph[current])
            {
                if (visited[neighbor] == 0)
                {
                    DfsTopo(neighbor, visited, stack);
                }
                else if (visited[neighbor] == 1)
                {
                    // Cycle detected: neighbor is currently being visited
                    // We do NOT throw an exception, just skip it to continue building partial order
                }
            }

            visited[current] = 2; // mark 'visited'
            stack.Push(current);
        }

        #endregion

        #region DFS Collect & Dependency Logic

        /// <summary>
        /// Standard DFS that collects all descendants of <paramref name="startNode"/> into <see cref="_result"/>.
        /// </summary>
        private void DfsCollect(int startNode)
        {
            if (_visited.Contains(startNode)) return;

            _visited.Add(startNode);
            _result.Add(startNode);

            if (_graph.ContainsKey(startNode))
            {
                foreach (int neighbor in _graph[startNode])
                {
                    DfsCollect(neighbor);
                }
            }
        }

        /// <summary>
        /// Recursively visits all descendants of <paramref name="startNode"/> (for data-staleness).
        /// The visited set is kept separately from <see cref="_visited"/> to avoid re-purposing.
        /// </summary>
        private List<int> DfsDescendants(int startNode, List<int> res, HashSet<int> visit)
        {
            if (!visit.Contains(startNode))
            {
                visit.Add(startNode);
                res.Add(startNode);

                if (_graph.ContainsKey(startNode))
                {
                    foreach (int neighbor in _graph[startNode])
                    {
                        DfsDescendants(neighbor, res, visit);
                    }
                }
            }
            return res;
        }

        /// <summary>
        /// Marks rows in <paramref name="table"/> as DataStatus=0 if their FromObjectMK or ToObjectMK is in <paramref name="values"/>.
        /// </summary>
        private void UpdateDataStatus(DataTable table, List<int> values)
        {
            foreach (DataRow row in table.Rows)
            {
                int fromObjectMK = Convert.ToInt32(row["FromObjectMK"]);
                int toObjectMK = Convert.ToInt32(row["ToObjectMK"]);

                // If it's in the "stale" list, mark DataStatus=0
                if (values.Contains(fromObjectMK) || values.Contains(toObjectMK))
                {
                    // This sets the row's DataStatus to integer 0, meaning false.
                    row["DataStatus"] = 0;
                }
            }
        }

        /// <summary>
        /// Checks if <paramref name="node"/> is a direct dependency for any node in <see cref="_result"/>.
        /// A "dependency" means node -> X, where X is in _result.
        /// </summary>
        private bool IsDependencyForAny(int node)
        {
            foreach (DataRow row in _lineageTable.Rows)
            {
                int from = row.Field<int>("FromObjectMK");
                int to = row.Field<int>("ToObjectMK");

                // If "to" is already in our result, and "from" is the candidate
                if (_result.Contains(to) && from == node)
                {
                    return true;
                }
            }
            return false;
        }

        ///// <summary>
        ///// Checks if <paramref name="node"/> is a 'skey' dependency for any node in _result.
        ///// i.e. node -> X, where X is in _result, and the node's name contains 'skey'.
        ///// </summary>
        //private bool IsSkeyDep(int node)
        //{
        //    foreach (DataRow row in _lineageTable.Rows)
        //    {
        //        int from = row.Field<int>("FromObjectMK");
        //        int to = row.Field<int>("ToObjectMK");

        //        if (_result.Contains(to)
        //            && from == node
        //            && row["FromObject"].ToString().Contains("skey", StringComparison.OrdinalIgnoreCase))
        //        {
        //            return true;
        //        }
        //    }
        //    return false;
        //}

        ///// <summary>
        ///// Checks if <paramref name="node"/> is an 'mkey' dependency for any node in _result.
        ///// i.e. node -> X, where X is in _result, and the node's name contains 'mkey'.
        ///// </summary>
        //private bool IsMkeyDep(int node)
        //{
        //    foreach (DataRow row in _lineageTable.Rows)
        //    {
        //        int from = row.Field<int>("FromObjectMK");
        //        int to = row.Field<int>("ToObjectMK");

        //        if (_result.Contains(to)
        //            && from == node
        //            && row["FromObject"].ToString().Contains("mkey", StringComparison.OrdinalIgnoreCase))
        //        {
        //            return true;
        //        }
        //    }
        //    return false;
        //}

        #endregion

        #region Node Level Calculation

        /// <summary>
        /// Helper class to represent a directed edge in the intermediate steps of CalculateNodeLevels.
        /// </summary>
        private class LineageNodeConnection
        {
            public int To { get; set; }
            public int FlowID { get; set; }
        }

        /// <summary>
        /// Represents metadata for a single node (ObjectMK) in the lineage graph.
        /// </summary>
        public class LineageNodeData
        {
            public int Level { get; set; }
            public int FlowID { get; set; }
            public string ColorHex { get; set; }
            public string ObjectName { get; set; }
            public string Batch { get; set; }
            public string FlowType { get; set; }
            public string SysAlias { get; set; }
            public DateTime LastExec { get; set; }
            public string LatestFileProcessed { get; set; }

            /// <summary>
            /// Node-level 'Status' from the table, e.g. 0/1/-1 meaning success/failure/unknown.
            /// </summary>
            public int Status { get; set; }

            /// <summary>
            /// DataStatus as a bool. The table usually contains 0/1, we interpret them as false/true.
            /// </summary>
            public bool DataStatus { get; set; }

            public LineageNodeData(int level, int flowID, string colorHex)
            {
                Level = level;
                FlowID = flowID;
                ColorHex = colorHex;

                // Default values
                ObjectName = string.Empty;
                Batch = string.Empty;
                FlowType = string.Empty;
                SysAlias = string.Empty;
                LastExec = DateTime.MinValue;
                LatestFileProcessed = string.Empty;
                Status = -1;
                DataStatus = false;
            }
        }

        /// <summary>
        /// Generates a dictionary of node -> lineage data, computing each node's Level, FlowID, Color, etc.
        /// The BFS-based approach also tries to handle isolated nodes, cycles, and fallback logic.
        /// </summary>
        internal static Dictionary<int, LineageNodeData> CalculateNodeLevels(DataTable lineageTable)
        {
            // 1) Build local adjacency (graph + reverseGraph + inDegree)
            var graph = new Dictionary<int, List<LineageNodeConnection>>();
            var reverseGraph = new Dictionary<int, List<LineageNodeConnection>>();
            var inDegree = new Dictionary<int, int>();
            var allNodes = new HashSet<int>();

            // flowInfo: FlowID -> (Batch, FlowType)
            var flowInfo = new Dictionary<int, Tuple<string, string>>();
            // nodeMetadata: nodeID -> property dictionary
            var nodeMetadata = new Dictionary<int, Dictionary<string, object>>();

            // Populate graphs
            foreach (DataRow row in lineageTable.Rows)
            {
                int flowID = row.Field<int>("FlowID");
                string batch = row.Field<string>("Batch");
                string flowType = row.Field<string>("FlowType");
                int fromNode = row.Field<int>("FromObjectMK");
                int toNode = row.Field<int>("ToObjectMK");

                // Track flow info
                if (!flowInfo.ContainsKey(flowID))
                {
                    flowInfo[flowID] = new Tuple<string, string>(batch, flowType);
                }

                // Update adjacency
                if (!graph.ContainsKey(fromNode)) graph[fromNode] = new List<LineageNodeConnection>();
                if (!reverseGraph.ContainsKey(toNode)) reverseGraph[toNode] = new List<LineageNodeConnection>();
                if (!inDegree.ContainsKey(fromNode)) inDegree[fromNode] = 0;
                if (!inDegree.ContainsKey(toNode)) inDegree[toNode] = 0;

                graph[fromNode].Add(new LineageNodeConnection { To = toNode, FlowID = flowID });
                reverseGraph[toNode].Add(new LineageNodeConnection { To = fromNode, FlowID = flowID });
                inDegree[toNode]++;

                // Track all nodes
                allNodes.Add(fromNode);
                allNodes.Add(toNode);

                // Node metadata for "fromNode" (often the authoritative info)
                if (!nodeMetadata.ContainsKey(fromNode))
                {
                    nodeMetadata[fromNode] = new Dictionary<string, object>();
                }
                nodeMetadata[fromNode]["ObjectName"] = row["FromObject"].ToString();
                nodeMetadata[fromNode]["Batch"] = batch;
                nodeMetadata[fromNode]["FlowType"] = flowType;
                nodeMetadata[fromNode]["FlowID"] = flowID;

                if (row.Table.Columns.Contains("SysAlias"))
                    nodeMetadata[fromNode]["SysAlias"] = row["SysAlias"].ToString();
                if (row.Table.Columns.Contains("LastExec") && row["LastExec"] != DBNull.Value)
                    nodeMetadata[fromNode]["LastExec"] = Convert.ToDateTime(row["LastExec"]);
                if (row.Table.Columns.Contains("LatestFileProcessed"))
                    nodeMetadata[fromNode]["LatestFileProcessed"] = row["LatestFileProcessed"].ToString();
                if (row.Table.Columns.Contains("Status"))
                    nodeMetadata[fromNode]["Status"] = Convert.ToInt32(row["Status"]);
                if (row.Table.Columns.Contains("DataStatus"))
                {
                    // interpret 0/1 or string "True"/"False"
                    bool ds = (row["DataStatus"].ToString() == "1" ||
                               row["DataStatus"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
                    nodeMetadata[fromNode]["DataStatus"] = ds;
                }

                // Node metadata for "toNode" (if not already set from a prior row)
                if (!nodeMetadata.ContainsKey(toNode))
                {
                    nodeMetadata[toNode] = new Dictionary<string, object>();
                    nodeMetadata[toNode]["ObjectName"] = row["ToObject"].ToString();
                    nodeMetadata[toNode]["Batch"] = batch;
                    nodeMetadata[toNode]["FlowType"] = flowType;
                    nodeMetadata[toNode]["FlowID"] = flowID;

                    if (row.Table.Columns.Contains("SysAlias"))
                        nodeMetadata[toNode]["SysAlias"] = row["SysAlias"].ToString();
                    if (row.Table.Columns.Contains("LastExec") && row["LastExec"] != DBNull.Value)
                        nodeMetadata[toNode]["LastExec"] = Convert.ToDateTime(row["LastExec"]);
                    if (row.Table.Columns.Contains("LatestFileProcessed"))
                        nodeMetadata[toNode]["LatestFileProcessed"] = row["LatestFileProcessed"].ToString();
                    if (row.Table.Columns.Contains("Status"))
                        nodeMetadata[toNode]["Status"] = Convert.ToInt32(row["Status"]);
                    if (row.Table.Columns.Contains("DataStatus"))
                    {
                        bool ds = (row["DataStatus"].ToString() == "1" ||
                                   row["DataStatus"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase));
                        nodeMetadata[toNode]["DataStatus"] = ds;
                    }
                }
            }

            // 2) Create default NodeData with level=0, color=gray, etc.
            var nodeLevelInfo = new Dictionary<int, LineageNodeData>();
            foreach (int node in allNodes)
            {
                var nd = new LineageNodeData(0, -1, "#808080");
                // Fill metadata if we have it
                if (nodeMetadata.ContainsKey(node))
                {
                    var m = nodeMetadata[node];
                    if (m.ContainsKey("ObjectName")) nd.ObjectName = m["ObjectName"].ToString();
                    if (m.ContainsKey("Batch")) nd.Batch = m["Batch"].ToString();
                    if (m.ContainsKey("FlowType")) nd.FlowType = m["FlowType"].ToString();
                    if (m.ContainsKey("SysAlias")) nd.SysAlias = m["SysAlias"].ToString();
                    if (m.ContainsKey("LastExec")) nd.LastExec = (DateTime)m["LastExec"];
                    if (m.ContainsKey("LatestFileProcessed"))
                        nd.LatestFileProcessed = m["LatestFileProcessed"].ToString();
                    if (m.ContainsKey("Status")) nd.Status = (int)m["Status"];
                    if (m.ContainsKey("DataStatus")) nd.DataStatus = (bool)m["DataStatus"];
                }
                nodeLevelInfo[node] = nd;
            }

            // 3) Find all source nodes for BFS (inDegree=0). If none, pick nodes with minimal inDegree
            var sourceNodes = inDegree.Where(kvp => kvp.Value == 0).Select(k => k.Key).ToList();
            if (sourceNodes.Count == 0)
            {
                int minInd = inDegree.Values.Min();
                sourceNodes = inDegree.Where(kvp => kvp.Value == minInd).Select(k => k.Key).ToList();
            }

            // 4) BFS from each source node, assigning level
            var nodeToLevel = new Dictionary<int, int>();
            foreach (int src in sourceNodes)
            {
                var queue = new Queue<(int node, int level)>();
                queue.Enqueue((src, 1));
                nodeToLevel[src] = 1;

                while (queue.Count > 0)
                {
                    (int current, int level) = queue.Dequeue();

                    // Update nodeLevelInfo
                    if (level > nodeLevelInfo[current].Level)
                    {
                        nodeLevelInfo[current].Level = level;

                        // Attempt to get FlowID from the node's own metadata
                        int candidateFlowID = -1;
                        if (nodeMetadata.ContainsKey(current) && nodeMetadata[current].ContainsKey("FlowID"))
                        {
                            candidateFlowID = Convert.ToInt32(nodeMetadata[current]["FlowID"]);
                        }
                        else if (graph.ContainsKey(current) && graph[current].Any())
                        {
                            candidateFlowID = graph[current].First().FlowID;
                        }

                        nodeLevelInfo[current].FlowID = candidateFlowID;

                        // Derive color from FlowID if we can
                        if (candidateFlowID != -1 && flowInfo.ContainsKey(candidateFlowID))
                        {
                            string cBatch = flowInfo[candidateFlowID].Item1;
                            string cFlowType = flowInfo[candidateFlowID].Item2;
                            string colorBase = cBatch + cFlowType;
                            nodeLevelInfo[current].ColorHex = ColorGenerator.GetBlockColor(colorBase);
                        }
                    }

                    // Enqueue children
                    if (graph.ContainsKey(current))
                    {
                        foreach (LineageNodeConnection child in graph[current])
                        {
                            int nextLevel = level + 1;
                            int childNode = child.To;

                            if (!nodeToLevel.ContainsKey(childNode) || nextLevel > nodeToLevel[childNode])
                            {
                                nodeToLevel[childNode] = nextLevel;
                                queue.Enqueue((childNode, nextLevel));
                            }
                        }
                    }
                }
            }

            // 5) For nodes still at FlowID==-1, try to adopt parent's FlowID
            foreach (int node in allNodes.Where(n => nodeLevelInfo[n].FlowID == -1))
            {
                if (reverseGraph.ContainsKey(node))
                {
                    foreach (var parentConn in reverseGraph[node])
                    {
                        int parent = parentConn.To;
                        int pFlowID = nodeLevelInfo[parent].FlowID;
                        if (pFlowID != -1)
                        {
                            // found a valid parent flow
                            nodeLevelInfo[node].FlowID = pFlowID;
                            if (flowInfo.ContainsKey(pFlowID))
                            {
                                string cBatch = flowInfo[pFlowID].Item1;
                                string cFlowType = flowInfo[pFlowID].Item2;
                                string colorBase = cBatch + cFlowType;
                                nodeLevelInfo[node].ColorHex = ColorGenerator.GetBlockColor(colorBase);
                                nodeLevelInfo[node].Batch = cBatch;
                                nodeLevelInfo[node].FlowType = cFlowType;
                            }
                            break;
                        }
                    }
                    // If still -1, try using parent's edge FlowID
                    if (nodeLevelInfo[node].FlowID == -1)
                    {
                        foreach (var parentConn in reverseGraph[node])
                        {
                            if (parentConn.FlowID != -1)
                            {
                                int edgeFlowID = parentConn.FlowID;
                                nodeLevelInfo[node].FlowID = edgeFlowID;
                                if (flowInfo.ContainsKey(edgeFlowID))
                                {
                                    string cBatch = flowInfo[edgeFlowID].Item1;
                                    string cFlowType = flowInfo[edgeFlowID].Item2;
                                    string colorBase = cBatch + cFlowType;
                                    nodeLevelInfo[node].ColorHex = ColorGenerator.GetBlockColor(colorBase);
                                    nodeLevelInfo[node].Batch = cBatch;
                                    nodeLevelInfo[node].FlowType = cFlowType;
                                }
                                break;
                            }
                        }
                    }
                }
            }

            // 6) Ensure no node is left at Level=0 => set to level=1
            foreach (int node in allNodes)
            {
                if (nodeLevelInfo[node].Level == 0)
                {
                    nodeLevelInfo[node].Level = 1;
                }
            }

            // 7) Special pass for "skey.*" nodes => set them one level above the children
            foreach (int node in allNodes)
            {
                string objName = nodeLevelInfo[node].ObjectName;
                if (!string.IsNullOrEmpty(objName) && objName.Contains("skey.", StringComparison.OrdinalIgnoreCase))
                {
                    // check its children
                    if (graph.ContainsKey(node) && graph[node].Count > 0)
                    {
                        int minChildLevel = int.MaxValue;
                        foreach (var child in graph[node])
                        {
                            int cLevel = nodeLevelInfo[child.To].Level;
                            if (cLevel < minChildLevel)
                            {
                                minChildLevel = cLevel;
                            }
                        }
                        if (minChildLevel < int.MaxValue)
                        {
                            nodeLevelInfo[node].Level = Math.Max(1, minChildLevel - 1);
                        }
                    }
                }
            }

            return nodeLevelInfo;
        }

        #endregion
    }
}
