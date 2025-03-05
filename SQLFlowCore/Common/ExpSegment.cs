using System;

namespace SQLFlowCore.Common
{
    internal class ExpSegment
    {
        internal string SqlCMD { get; set; }
        internal string WhereClause { get; set; }
        internal string FilePath_DW { get; set; }
        internal string FileSubFolder_DW { get; set; }
        internal string FileName_DW { get; set; }
        internal string FileType_DW { get; set; }
        internal long FileSize_DW { get; set; }
        internal long FileRows_DW { get; set; }
        internal long MaxKeyValue { get; set; }

        internal DateTime NextExportDate { get; set; }

        internal int NextExportValue { get; set; }

        // Constructor with default value for NextExportDate
        internal ExpSegment()
        {
            NextExportDate = FlowDates.Default;
            NextExportValue = 0;
        }


        internal string GetFileNameWithPath()
        {
            return FilePath_DW + FileSubFolder_DW + FileName_DW + "." + FileType_DW;
        }

        internal string GetFullPath()
        {
            return FilePath_DW + FileSubFolder_DW;
        }

    }
}
