using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;

namespace SQLFlowCore.Lineage
{
    /// <summary>
    /// Represents the path information in a lineage graph.
    /// </summary>
    /// <remarks>
    /// This class is used to store and manage the path information in a lineage graph,
    /// including the path number, path string, circularity status, level, and flow ID.
    /// </remarks>
    internal class PathInfo
    {
        internal string PathNum { get; set; }
        internal string PathStr { get; set; }
        internal bool IsCircular { get; set; }
        internal int Level { get; set; }
        internal int FlowID { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is PathInfo other)
            {
                return PathNum == other.PathNum
                       && PathStr == other.PathStr
                       && IsCircular == other.IsCircular
                       && Level == other.Level;
            }
            return false;
        }

        public override int GetHashCode()
        {
            int hash = 17; // some prime number
            hash = hash * 23 + (PathNum?.GetHashCode() ?? 0);
            hash = hash * 23 + (PathStr?.GetHashCode() ?? 0);
            hash = hash * 23 + IsCircular.GetHashCode();
            hash = hash * 23 + Level.GetHashCode();
            return hash;
        }
    }

    /// <summary>
    /// Represents a node in a lineage graph.
    /// </summary>
    /// <remarks>
    /// This class is used to store and manage information about a node in a lineage graph, 
    /// including the number of children, root object marker, maximum level, node marker, 
    /// parent node marker, flow ID, edge, and timestamps for the last updates of the node and its parent.
    /// </remarks>
    internal class NodeInfo
    {
        internal int NoOfChildren { get; set; }
        internal int RootObjectMK { get; set; }
        internal int MaxLevel { get; set; }

        internal int NodeMK { get; set; }
        internal int ParentNodeMK { get; set; }

        internal int FlowID { get; set; }
        internal (int, int) Edge { get; set; }

        internal DateTime NodeLastUpdated { get; set; }
        internal DateTime ParentNodeLastUpdated { get; set; }
    }

    /// <summary>
    /// Represents the execution information of a flow in a lineage graph.
    /// </summary>
    /// <remarks>
    /// This class is used to store and manage information about the execution of a flow in a lineage graph,
    /// including the flow ID, markers for the originating and destination objects, and the timestamp for the last update of the edge.
    /// </remarks>
    internal class FlowExecInfo
    {
        internal int FlowID { get; set; }
        internal int FromObjectMK { get; set; }
        internal int ToObjectMK { get; set; }
        internal DateTime EdgeLastUpdated { get; set; }
    }

    /// <summary>
    /// The LineageParser class is responsible for parsing lineage data.
    /// </summary>
    /// <remarks>
    /// This class takes a DataTable as input and processes it to build node names, edge flow IDs, and update node information.
    /// It also provides methods to get the result of the parsing, get next flow IDs, and handle path information.
    /// </remarks>
    internal class LineageParser
    {
        /// <summary>
        /// Graph representation (Adjacency List):
        /// Key = FromNode, Value = List of ToNodes.
        /// </summary>
        private readonly Dictionary<int, List<int>> _graphBase = new();

        /// <summary>
        /// Dictionary storing paths for each edge. 
        /// Key = (FromObjectMK, ToObjectMK), Value = List of PathInfo objects.
        /// </summary>
        private readonly Dictionary<(int, int), List<PathInfo>> _pathDict = new();

        /// <summary>
        /// Dictionary storing node information. 
        /// Key = Node MK, Value = NodeInfo object.
        /// </summary>
        private readonly Dictionary<int, NodeInfo> _nodeInfoDict = new();

        /// <summary>
        /// Dictionary storing node IDs and their names. 
        /// Key = Node ID, Value = Node Name.
        /// </summary>
        private readonly Dictionary<int, string> _nodeNames = new();

        /// <summary>
        /// Dictionary storing flow ID information for edges. 
        /// Key = (FromObjectMK, ToObjectMK), Value = Flow ID.
        /// </summary>
        private readonly Dictionary<(int, int), int> _edgeFlowID = new();

        /// <summary>
        /// For quick FlowID -> Edge lookups (avoid expensive .FirstOrDefault searches).
        /// </summary>
        private readonly Dictionary<int, (int fromNode, int toNode)> _flowIdToEdge = new();

        /// <summary>
        /// Dictionary to store the maximum level per root node. 
        /// Key = Root Node MK, Value = Max Level (depth).
        /// </summary>
        private readonly Dictionary<int, int> _maxLevelDict = new();

        /// <summary>
        /// DataTable which holds the lineage data. 
        /// </summary>
        private readonly DataTable _lineageTable;

        /// <summary>
        /// Initializes a new instance of the <see cref="LineageParser"/> class.
        /// </summary>
        /// <param name="_lineageTable">The DataTable containing lineage data.</param>
        /// <remarks>
        /// This constructor initializes the LineageParser with a DataTable, builds node names and edge flow IDs, 
        /// updates node information, and parses the graph. It also calculates the Next Step FlowIDs and
        /// populates new columns for each row in the provided DataTable.
        /// </remarks>
        internal LineageParser(DataTable _lineageTable)
        {
            // Keep a reference to the incoming DataTable
            this._lineageTable = _lineageTable;

            // 1) Basic structures
            BuildNodeNames(_nodeNames, this._lineageTable);
            BuildEdgeFlowID(_edgeFlowID, this._lineageTable);

            // 2) Also fill the helper dictionary for quick FlowID -> Edge lookup
            foreach (var kvp in _edgeFlowID)
            {
                // kvp.Key = (fromObjectMK, toObjectMK); kvp.Value = flowID
                _flowIdToEdge[kvp.Value] = kvp.Key;
            }

            // 3) Initialize Node Info
            UpdateNodeInfoDic(_nodeInfoDict, this._lineageTable);

            // 4) Parse to build the adjacency list, detect cycles, levels, etc.
            Parse(
                _graphBase,
                this._lineageTable,
                _nodeNames,
                _maxLevelDict,
                _pathDict,
                _nodeInfoDict,
                _edgeFlowID
            );

            // 5) Populate the DataTable columns (PathNum, PathStr, Level, Step, Circular, NoOfChildren, RootObjectMK, RootObject, MaxLevel)
            foreach (DataRow row in this._lineageTable.Rows)
            {
                int fromObjectMK = (int)row["FromObjectMK"];
                int toObjectMK = (int)row["ToObjectMK"];
                var edge = (fromObjectMK, toObjectMK);

                // Safely retrieve any path info for this edge
                if (_pathDict.TryGetValue(edge, out var pathObjects) && pathObjects != null && pathObjects.Count > 0)
                {
                    var largestLevel = GetPathWithLargestLevel(pathObjects);
                    bool isCircular = ContainsCircularEdge(pathObjects);
                    string pathNum = CombinePathNums(pathObjects);
                    string pathStr = CombinePathStrs(pathObjects);

                    row["PathNum"] = pathNum;
                    row["PathStr"] = pathStr;
                    row["Level"] = largestLevel.Level;
                    // "Step" example logic
                    row["Step"] = 100 * largestLevel.Level;
                    row["Circular"] = isCircular;
                }
                else
                {
                    // If there's no recorded path, set them to defaults or empty
                    row["PathNum"] = string.Empty;
                    row["PathStr"] = string.Empty;
                    row["Level"] = 0;
                    row["Step"] = 0;
                    row["Circular"] = false;
                }

                // Fill node info (from side)
                if (_nodeInfoDict.ContainsKey(fromObjectMK))
                {
                    row["NoOfChildren"] = _nodeInfoDict[fromObjectMK].NoOfChildren;
                    row["RootObjectMK"] = _nodeInfoDict[fromObjectMK].RootObjectMK;

                    // Try to get the root node name
                    if (_nodeNames.TryGetValue(_nodeInfoDict[fromObjectMK].RootObjectMK, out var rootName))
                    {
                        row["RootObject"] = rootName;
                    }
                    else
                    {
                        row["RootObject"] = string.Empty;
                    }
                }

                // Fill max level if available
                int root = _nodeInfoDict.ContainsKey(fromObjectMK)
                           ? _nodeInfoDict[fromObjectMK].RootObjectMK
                           : fromObjectMK;

                if (_maxLevelDict.ContainsKey(root))
                {
                    row["MaxLevel"] = _maxLevelDict[root];
                }
                else
                {
                    row["MaxLevel"] = 0;
                }
            }

            // 6) Calculate the Next Step FlowIDs for each row
            foreach (DataRow row in this._lineageTable.Rows)
            {
                int flowID = (int)row["FlowID"];
                List<int> nextFlowIds = GetNextFlowIDs(flowID);
                string commaSeparatedList = string.Join(",", nextFlowIds);
                row["NextStepFlows"] = commaSeparatedList;
            }
        }

        /// <summary>
        /// Retrieves the result of the lineage parsing process.
        /// </summary>
        /// <returns>A DataTable containing the result of the lineage parsing.</returns>
        internal DataTable GetResult()
        {
            return _lineageTable;
        }

        /// <summary>
        /// Retrieves the list of next FlowIDs for a given FlowID.
        /// </summary>
        /// <param name="flowID">The FlowID to find the next FlowIDs for.</param>
        /// <returns>
        /// A list of integers representing the next FlowIDs. 
        /// If no next FlowIDs are found, an empty list is returned.
        /// The list is distinct, meaning it does not contain duplicate FlowIDs.
        /// </returns>
        /// <remarks>
        /// This method leverages the prebuilt _flowIdToEdge dictionary to avoid O(n) lookups.
        /// It then looks up all edges from the 'toNode' in the adjacency list _graphBase.
        /// </remarks>
        internal List<int> GetNextFlowIDs(int flowID)
        {
            var nextFlowIDs = new List<int>();

            // Quickly find the edge by flowID
            if (_flowIdToEdge.TryGetValue(flowID, out var currentEdge))
            {
                // currentEdge = (fromNode, toNode)
                // find all edges that start from currentEdge.toNode
                int from = currentEdge.fromNode;
                int to = currentEdge.toNode;

                if (_graphBase.ContainsKey(to))
                {
                    // for each neighbor of 'to'
                    foreach (var nextNode in _graphBase[to])
                    {
                        // try to get the flow ID for (to, nextNode)
                        if (_edgeFlowID.TryGetValue((to, nextNode), out int nextFlowId))
                        {
                            // avoid re-adding the same flowID
                            if (nextFlowId != flowID)
                            {
                                nextFlowIDs.Add(nextFlowId);
                            }
                        }
                    }
                }
            }

            return nextFlowIDs.Distinct().ToList();
        }

        /// <summary>
        /// Retrieves the path with the highest level from a list of PathInfo objects.
        /// </summary>
        /// <param name="paths">A list of PathInfo objects.</param>
        /// <returns>
        /// The PathInfo object with the highest level. 
        /// If the list is empty or null, returns null.
        /// </returns>
        internal static PathInfo GetPathWithLargestLevel(List<PathInfo> paths)
        {
            if (paths == null || paths.Count == 0)
            {
                return null;
            }

            PathInfo maxLevelPath = paths[0];
            foreach (var path in paths)
            {
                if (path.Level > maxLevelPath.Level)
                {
                    maxLevelPath = path;
                }
            }
            return maxLevelPath;
        }

        /// <summary>
        /// Combines the path numbers of the provided list of PathInfo objects into a single string.
        /// </summary>
        /// <param name="paths">A list of PathInfo objects whose path numbers are to be combined.</param>
        /// <returns>
        /// A string representing the combined path numbers. Each path number is separated by '|'. 
        /// If the provided list is null or empty, an empty string is returned.
        /// </returns>
        internal static string CombinePathNums(List<PathInfo> paths)
        {
            if (paths == null || !paths.Any())
            {
                return string.Empty;
            }

            // Use a hash set to ensure we don't combine duplicates
            HashSet<PathInfo> uniquePaths = new HashSet<PathInfo>(paths);

            StringBuilder combinedPathNums = new StringBuilder();
            foreach (var path in uniquePaths)
            {
                if (combinedPathNums.Length > 0)
                {
                    combinedPathNums.Append("|");
                }
                combinedPathNums.Append(path.PathNum);
            }
            return combinedPathNums.ToString();
        }

        /// <summary>
        /// Combines the path strings of the provided list of PathInfo objects into a single string.
        /// </summary>
        /// <param name="paths">A list of PathInfo objects whose path strings are to be combined.</param>
        /// <returns>
        /// A string that represents the combined path strings of the provided PathInfo objects. 
        /// Each path string is separated by a "|" character. 
        /// If the provided list is null or empty, an empty string is returned.
        /// </returns>
        internal static string CombinePathStrs(List<PathInfo> paths)
        {
            if (paths == null || !paths.Any())
            {
                return string.Empty;
            }

            HashSet<PathInfo> uniquePaths = new HashSet<PathInfo>(paths);

            StringBuilder combinedPathStrs = new StringBuilder();
            foreach (var path in uniquePaths)
            {
                if (combinedPathStrs.Length > 0)
                {
                    combinedPathStrs.Append("|");
                }
                combinedPathStrs.Append(path.PathStr);
            }
            return combinedPathStrs.ToString();
        }

        /// <summary>
        /// Determines whether any path in the provided list contains a circular edge.
        /// </summary>
        /// <param name="paths">A list of PathInfo objects representing the paths to be checked for circular edges.</param>
        /// <returns>True if any path in the list contains a circular edge; otherwise, false.</returns>
        internal static bool ContainsCircularEdge(List<PathInfo> paths)
        {
            if (paths == null || !paths.Any())
            {
                return false;
            }

            return paths.Any(path => path.IsCircular);
        }

        /// <summary>
        /// Performs a topological sort on the given graph.
        /// </summary>
        /// <param name="graph">The graph to be sorted, represented as an adjacency list 
        /// where each key is a node and the value is a list of nodes that the key node points to.</param>
        /// <returns>A list of integers representing the nodes of the graph in a topologically sorted order.</returns>
        /// <remarks>
        /// If the graph contains cycles, this sort may not reflect a purely valid topological ordering 
        /// for the entire graph. However, it will sort the acyclic portions.
        /// </remarks>
        private static List<int> TopologicalSort(Dictionary<int, List<int>> graph)
        {
            List<int> result = new List<int>();
            HashSet<int> visited = new HashSet<int>();
            Stack<int> stack = new Stack<int>();

            foreach (var node in graph.Keys)
            {
                if (!visited.Contains(node))
                {
                    TopologicalSortUtil(node, visited, stack, graph);
                }
            }

            while (stack.Count > 0)
            {
                result.Add(stack.Pop());
            }

            return result;
        }

        /// <summary>
        /// Recursively performs a topological sort on the graph from a specific node.
        /// </summary>
        /// <param name="node">The node from which to start.</param>
        /// <param name="visited">A set of visited nodes.</param>
        /// <param name="stack">A stack for maintaining sort order.</param>
        /// <param name="graph">Adjacency list for the graph.</param>
        private static void TopologicalSortUtil(
            int node,
            HashSet<int> visited,
            Stack<int> stack,
            Dictionary<int, List<int>> graph
        )
        {
            visited.Add(node);

            if (graph.ContainsKey(node))
            {
                foreach (var neighbor in graph[node])
                {
                    if (!visited.Contains(neighbor))
                    {
                        TopologicalSortUtil(neighbor, visited, stack, graph);
                    }
                }
            }

            stack.Push(node);
        }

        /// <summary>
        /// Parses the provided DataTable and constructs a graph representation of the data lineage,
        /// then performs DFS to detect cycles, build path info, and compute levels.
        /// Finally, calculates the number of children for each root node.
        /// </summary>
        /// <param name="graphBase">The base graph to be populated with nodes and edges from the DataTable.</param>
        /// <param name="table">The DataTable containing the data lineage information.</param>
        /// <param name="nodeNames">A dictionary to store the names of the nodes.</param>
        /// <param name="maxLevelDict">A dictionary to store the maximum level of each root node.</param>
        /// <param name="pathDict">A dictionary to store the paths in the graph.</param>
        /// <param name="nodeInfoDict">A dictionary to store additional information about each node.</param>
        /// <param name="edgeFlowID">A dictionary to store the flow ID of each edge.</param>
        private static void Parse(
            Dictionary<int, List<int>> graphBase,
            DataTable table,
            Dictionary<int, string> nodeNames,
            Dictionary<int, int> maxLevelDict,
            Dictionary<(int, int), List<PathInfo>> pathDict,
            Dictionary<int, NodeInfo> nodeInfoDict,
            Dictionary<(int, int), int> edgeFlowID
        )
        {
            // Build adjacency list from DataTable
            foreach (DataRow row in table.Rows)
            {
                int fromObjectMK = (int)row["FromObjectMK"];
                int toObjectMK = (int)row["ToObjectMK"];

                if (!graphBase.ContainsKey(fromObjectMK))
                    graphBase[fromObjectMK] = new List<int>();

                graphBase[fromObjectMK].Add(toObjectMK);

                // Ensure 'to' node is also in graph, even if it has no children
                if (!graphBase.ContainsKey(toObjectMK))
                    graphBase[toObjectMK] = new List<int>();
            }

            // We maintain a dictionary to track visiting states:
            // 0 = unvisited, 1 = visiting, 2 = visited
            var state = new Dictionary<int, int>();

            // Topological sort for an initial ordering (not strictly needed to detect cycles, 
            // but can be helpful for large DAG sections).
            List<int> sortedNodes = TopologicalSort(graphBase);

            // DFS each node (in topological order) to gather path info and detect cycles
            foreach (var node in sortedNodes)
            {
                if (!state.ContainsKey(node) || state[node] == 0)
                {
                    int maxLevel = 1;   // minimum level is 1
                    int initLevel = 0;
                    var currentPath = new List<int>();

                    DFS(
                        parentNode: -1,
                        node: node,
                        graph: graphBase,
                        state: state,
                        currentPath: currentPath,
                        nodeNames: nodeNames,
                        currentLevel: initLevel,
                        rootObjectMK: node, // each top-level node is considered a potential root
                        maxLevelDict: maxLevelDict,
                        maxLevel: ref maxLevel,
                        pathDict: pathDict,
                        nodeInfoDict: nodeInfoDict,
                        edgeFlowID: edgeFlowID
                    );

                    maxLevelDict[node] = maxLevel;

                    // Set the node's own MaxLevel in the info dictionary if it exists
                    if (nodeInfoDict.ContainsKey(node))
                    {
                        nodeInfoDict[node].MaxLevel = maxLevel;
                    }
                }
            }

            // After DFS, compute the number of descendants for each root node
            foreach (var node in graphBase.Keys)
            {
                // Check if 'node' is a root: no one points to it
                bool isRoot = true;
                foreach (var kvp in graphBase)
                {
                    // If kvp.Value (list of children) contains 'node', then 'node' is not a root
                    if (kvp.Value.Contains(node))
                    {
                        isRoot = false;
                        break;
                    }
                }

                // If it is root, do a DFS to count children
                if (isRoot)
                {
                    var visited = new HashSet<int>();
                    DFS2(nodeInfoDict, graphBase, node, 0, visited);
                }
            }
        }

        /// <summary>
        /// Depth-First Search to record path information, detect cycles, and assign root markers and levels.
        /// </summary>
        /// <param name="parentNode">Parent node in the DFS chain.</param>
        /// <param name="node">Current node.</param>
        /// <param name="graph">Adjacency list for the graph.</param>
        /// <param name="state">Dictionary of node states: 0=unvisited, 1=visiting, 2=visited.</param>
        /// <param name="currentPath">List capturing the current traversal path.</param>
        /// <param name="nodeNames">Dictionary of node IDs to node names.</param>
        /// <param name="currentLevel">Current level in the DFS stack.</param>
        /// <param name="rootObjectMK">Marker indicating the root object for this subgraph.</param>
        /// <param name="maxLevelDict">Dictionary storing the max depth for each root.</param>
        /// <param name="maxLevel">Reference to an integer representing the running max depth.</param>
        /// <param name="pathDict">Dictionary mapping edges to a list of PathInfo.</param>
        /// <param name="nodeInfoDict">Dictionary of NodeInfo objects.</param>
        /// <param name="edgeFlowID">Dictionary mapping (fromNode,toNode) to a Flow ID.</param>
        private static void DFS(
            int parentNode,
            int node,
            Dictionary<int, List<int>> graph,
            Dictionary<int, int> state,
            List<int> currentPath,
            Dictionary<int, string> nodeNames,
            int currentLevel,
            int rootObjectMK,
            Dictionary<int, int> maxLevelDict,
            ref int maxLevel,
            Dictionary<(int, int), List<PathInfo>> pathDict,
            Dictionary<int, NodeInfo> nodeInfoDict,
            Dictionary<(int, int), int> edgeFlowID
        )
        {
            // Mark the node as visiting
            state[node] = 1;

            // Add the node to the current path
            currentPath.Add(node);

            // Ensure we have a NodeInfo entry
            if (!nodeInfoDict.ContainsKey(node))
            {
                nodeInfoDict[node] = new NodeInfo();
            }

            // Update node info
            nodeInfoDict[node].RootObjectMK = rootObjectMK;
            nodeInfoDict[node].NodeMK = node;
            nodeInfoDict[node].ParentNodeMK = parentNode;
            nodeInfoDict[node].Edge = (parentNode, node);

            // Explore neighbors
            if (graph.ContainsKey(node))
            {
                foreach (var neighbor in graph[node])
                {
                    // Prepare a new PathInfo record for (node -> neighbor)
                    var pathInfo = new PathInfo
                    {
                        Level = currentLevel + 1
                    };

                    // If we have a flowID for this edge, set it
                    if (edgeFlowID.TryGetValue((node, neighbor), out int fID))
                    {
                        pathInfo.FlowID = fID;
                    }

                    // Detect cycles: If the neighbor is in the visiting state, it's a cycle.
                    if (state.ContainsKey(neighbor) && state[neighbor] == 1)
                    {
                        pathInfo.IsCircular = true;
                    }
                    else
                    {
                        // If the neighbor hasn't been in the current path, go deeper
                        if (!currentPath.Contains(neighbor))
                        {
                            int nextLevel = currentLevel + 1;
                            maxLevel = Math.Max(maxLevel, nextLevel);

                            // Recurse with a new copy of the path (to avoid side effects)
                            DFS(
                                parentNode: node,
                                node: neighbor,
                                graph: graph,
                                state: state,
                                currentPath: new List<int>(currentPath),
                                nodeNames: nodeNames,
                                currentLevel: nextLevel,
                                rootObjectMK: rootObjectMK,
                                maxLevelDict: maxLevelDict,
                                maxLevel: ref maxLevel,
                                pathDict: pathDict,
                                nodeInfoDict: nodeInfoDict,
                                edgeFlowID: edgeFlowID
                            );
                        }
                    }

                    // Build path strings
                    string pathNum = string.Join("->", currentPath.Concat(new[] { neighbor }));
                    pathInfo.PathNum = pathNum;

                    // Convert path to names
                    var nameSequence = pathNum
                        .Split(new[] { "->" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(n => int.TryParse(n, out var parsed) && nodeNames.ContainsKey(parsed)
                                     ? nodeNames[parsed]
                                     : n);
                    pathInfo.PathStr = string.Join("->", nameSequence);

                    // Add to pathDict
                    if (!pathDict.ContainsKey((node, neighbor)))
                    {
                        pathDict[(node, neighbor)] = new List<PathInfo>();
                    }
                    pathDict[(node, neighbor)].Add(pathInfo);
                }
            }

            // Mark the node as visited
            state[node] = 2;
        }

        /// <summary>
        /// DFS to compute the number of descendants for a given node, storing the result in the NodeInfo dictionary.
        /// </summary>
        /// <param name="nodeInfoDict">Dictionary of NodeInfo objects.</param>
        /// <param name="graph">Adjacency list.</param>
        /// <param name="node">Current node.</param>
        /// <param name="level">Current DFS level (not heavily used, but shown for clarity).</param>
        /// <param name="visited">Set of visited nodes to prevent double-counting.</param>
        /// <returns>Number of descendants (including the node itself).</returns>
        private static int DFS2(
            Dictionary<int, NodeInfo> nodeInfoDict,
            Dictionary<int, List<int>> graph,
            int node,
            int level,
            HashSet<int> visited
        )
        {
            // If we've already visited this node, do not recount
            if (!visited.Add(node))
            {
                return 0;
            }

            // If this node has no outgoing edges, the count is just 1 (itself)
            if (!graph.ContainsKey(node) || graph[node].Count == 0)
            {
                // Mark no children
                if (!nodeInfoDict.ContainsKey(node))
                {
                    nodeInfoDict[node] = new NodeInfo();
                }
                nodeInfoDict[node].NoOfChildren = 0;
                return 1;
            }

            // Count is 1 for the node itself
            int descendantsCount = 1;

            // Recurse on all children
            foreach (var neighbor in graph[node])
            {
                descendantsCount += DFS2(nodeInfoDict, graph, neighbor, level + 1, visited);
            }

            // Update NodeInfo to reflect total children (excluding the node itself)
            if (!nodeInfoDict.ContainsKey(node))
            {
                nodeInfoDict[node] = new NodeInfo();
            }
            nodeInfoDict[node].NoOfChildren = descendantsCount - 1;
            return descendantsCount;
        }

        /// <summary>
        /// Builds a dictionary of node names from the lineage table.
        /// </summary>
        /// <param name="nodeNames">A dictionary to be filled with node names.</param>
        /// <param name="lineageTable">The DataTable containing lineage data.</param>
        /// <remarks>
        /// This method iterates over each row in the lineage table, extracting the 'FromObjectMK' and 'ToObjectMK'
        /// as keys, and 'FromObject' and 'ToObject' as corresponding values. 
        /// </remarks>
        internal static void BuildNodeNames(Dictionary<int, string> nodeNames, DataTable lineageTable)
        {
            foreach (DataRow row in lineageTable.Rows)
            {
                int fromObjectMK = row["FromObjectMK"] != DBNull.Value
                                   ? (int)row["FromObjectMK"]
                                   : 0;
                string fromObjectName = row["FromObject"] != DBNull.Value
                                        ? (string)row["FromObject"]
                                        : string.Empty;

                int toObjectMK = row["ToObjectMK"] != DBNull.Value
                                 ? (int)row["ToObjectMK"]
                                 : 0;
                string toObjectName = row["ToObject"] != DBNull.Value
                                      ? (string)row["ToObject"]
                                      : string.Empty;

                if (!nodeNames.ContainsKey(fromObjectMK))
                {
                    nodeNames[fromObjectMK] = fromObjectName;
                }

                if (!nodeNames.ContainsKey(toObjectMK))
                {
                    nodeNames[toObjectMK] = toObjectName;
                }
            }
        }

        /// <summary>
        /// Builds the edge flow ID dictionary from the lineage table.
        /// </summary>
        /// <param name="edgeFlowID">The dictionary to be populated with edge flow IDs. 
        /// The key is a tuple (fromObjectMK, toObjectMK), and the value is the flow ID.</param>
        /// <param name="lineageTable">The DataTable containing the lineage data.</param>
        internal static void BuildEdgeFlowID(
            Dictionary<(int, int), int> edgeFlowID,
            DataTable lineageTable
        )
        {
            foreach (DataRow row in lineageTable.Rows)
            {
                int fromObjectMK = (int)row["FromObjectMK"];
                int toObjectMK = (int)row["ToObjectMK"];
                int flowID = (int)row["FlowID"];

                var edge = (fromObjectMK, toObjectMK);

                if (!edgeFlowID.ContainsKey(edge))
                {
                    edgeFlowID.Add(edge, flowID);
                }
            }
        }

        /// <summary>
        /// Builds a dictionary of FlowExecInfo objects from the provided lineage table.
        /// </summary>
        /// <param name="lineageTable">The DataTable containing lineage data.</param>
        /// <returns>A Dictionary where the key is (FromObjectMK, ToObjectMK), and the value is a FlowExecInfo object.</returns>
        /// <remarks>
        /// This method groups the lineage data by (FlowID, FromObjectMK, ToObjectMK). 
        /// For each group, it creates a FlowExecInfo object with the maximum LastExec value as EdgeLastUpdated.
        /// </remarks>
        internal Dictionary<(int, int), FlowExecInfo> BuildFlowExecInfoDictionary(DataTable lineageTable)
        {
            var groupedData = lineageTable.AsEnumerable()
                .GroupBy(row => (
                    FlowID: row.Field<int>("FlowID"),
                    FromObjectMK: row.Field<int>("FromObjectMK"),
                    ToObjectMK: row.Field<int>("ToObjectMK")
                ));

            var flowExecInfoDictionary = new Dictionary<(int, int), FlowExecInfo>();

            foreach (var group in groupedData)
            {
                var edgeLastUpdated = group.Max(row => row.Field<DateTime>("LastExec"));

                flowExecInfoDictionary[(group.Key.FromObjectMK, group.Key.ToObjectMK)] = new FlowExecInfo
                {
                    FlowID = group.Key.FlowID,
                    FromObjectMK = group.Key.FromObjectMK,
                    ToObjectMK = group.Key.ToObjectMK,
                    EdgeLastUpdated = edgeLastUpdated
                };
            }

            return flowExecInfoDictionary;
        }

        /// <summary>
        /// Updates the node information dictionary with data from the provided lineage table.
        /// </summary>
        /// <param name="nodeInfoDict">The dictionary to be updated, 
        /// where the key is the node marker and the value is the node information.</param>
        /// <param name="lineageTable">The DataTable containing lineage data.</param>
        internal static void UpdateNodeInfoDic(Dictionary<int, NodeInfo> nodeInfoDict, DataTable lineageTable)
        {
            foreach (DataRow row in lineageTable.Rows)
            {
                int fromObjectMK = (int)row["FromObjectMK"];
                int toObjectMK = (int)row["ToObjectMK"];

                // Ensure we have NodeInfo objects for both
                if (!nodeInfoDict.ContainsKey(fromObjectMK))
                {
                    nodeInfoDict[fromObjectMK] = new NodeInfo();
                }
                if (!nodeInfoDict.ContainsKey(toObjectMK))
                {
                    nodeInfoDict[toObjectMK] = new NodeInfo();
                }

                // Additional fields could be populated here if necessary, e.g. timestamps,
                // but for now, we just ensure the dictionary has entries.
            }
        }
    }
}
