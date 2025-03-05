using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SQLFlowCore.Common
{
    internal class SrcTargetColMap
    {
        private ConcurrentBag<FlowMap> concurrentItems = new();

        // This method will create a dictionary from the concurrent bag based on the FlowID
        internal Dictionary<string, string> GetMapDictionary(int flowId)
        {
            return concurrentItems.Where(item => item.FlowID == flowId)
                                  .ToDictionary(item => item.SourceColumn, item => item.TargetColumn);
        }

        internal void AddItem(FlowMap flowMap)
        {
            if (!concurrentItems.Contains(flowMap))
            {
                concurrentItems.Add(flowMap);
            }
        }

        internal bool ItemExists(FlowMap flowMap)
        {
            return concurrentItems.Contains(flowMap);
        }
    }


    internal class FlowMap
    {
        internal int FlowID { get; set; }
        internal string SourceColumn { get; set; }
        internal string TargetColumn { get; set; }

        // Override Equals and GetHashCode for proper comparison
        public override bool Equals(object obj)
        {
            return obj is FlowMap item &&
                   FlowID == item.FlowID &&
                   string.Equals(SourceColumn, item.SourceColumn, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(TargetColumn, item.TargetColumn, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(FlowID, SourceColumn, TargetColumn);
        }
    }






}
