using System.Collections.Generic;
using System.Data;

namespace SQLFlowCore.Common
{
    internal class DataTableSplitter
    {
        internal static List<DataTable> SplitDataTable(DataTable table, int chunkSize)
        {
            List<DataTable> chunks = new List<DataTable>();
            DataTable currentChunk = table.Clone();  // Clone only once

            int rowCount = 0;

            foreach (DataRow row in table.Rows)
            {
                currentChunk.ImportRow(row);
                rowCount++;

                if (rowCount == chunkSize)
                {
                    chunks.Add(currentChunk);
                    currentChunk = table.Clone(); // Re-clone structure for new chunk
                    rowCount = 0;
                }
            }

            // Add any remaining rows
            if (rowCount > 0)
            {
                chunks.Add(currentChunk);
            }

            return chunks;
        }
    }
}
