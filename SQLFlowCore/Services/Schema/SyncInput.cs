using System.Collections.Generic;
using System.Data;
using SQLFlowCore.Common;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents the input parameters for the synchronization process in SQLFlowCore.
    /// </summary>
    /// <remarks>
    /// This class contains various properties that control the behavior of the synchronization process, 
    /// such as source and target database details, column names to ignore, and various flags for controlling 
    /// the synchronization process. It also contains lists of system column names, invalid checksum data types, 
    /// and columns to ignore for checksum calculation.
    /// </remarks>
    internal class SyncInput
    {
        internal readonly List<string> InvalidChecksumDataTypes;
        internal readonly List<string> IgnoreChecksumColumns;
        internal readonly List<string> SysColNames;

        public string SrcDatabase { get; set; }
        public string SrcSchema { get; set; }
        public string SrcObject { get; set; }
        public string TrgDatabase { get; set; }
        public string TrgSchema { get; set; }
        public string TrgObject { get; set; }

        public bool trgVersioning { get; set; }

        public bool TrgIsStaging { get; set; }
        public bool SrcIsStaging { get; set; }

        internal bool TrgIsSynapse { get; set; }
        internal bool SrcIsSynapse { get; set; }

        internal string DateColumn { get; set; }

        internal string IdentityColumn { get; set; }

        internal string DataSetColumn { get; set; }
        internal EnhancedObjectNameList KeyColumnList { get; set; } = new();
        internal EnhancedObjectNameList IncrementalColumnList { get; set; } = new();
        internal EnhancedObjectNameList HashKeyColumnList { get; set; } = new();
        internal EnhancedObjectNameList IgnoreColumnList { get; set; } = new();

        internal EnhancedObjectNameList IgnoreColumnsInHashkey { get; set; } = new();




        internal string ReplaceInvalidCharsWith { get; set; } = "";

        internal bool SyncSchema { get; set; }
        internal bool CleanColumnName { get; set; }
        internal bool ConvUnicodeDt { get; set; }
        internal bool CreateIndexes { get; set; }
        internal string ColCleanupSqlRegExp { get; set; }
        internal int CommandTimeOutInSeconds { get; set; }

        internal DataTable TokenCols { get; set; }
        internal DataTable TransformTbl { get; set; }
        internal DataTable VirtualColsTbl { get; set; }

        internal SyncOutput SyncOutput { get; set; }

        internal SyncInput()
        {
            SyncOutput = new SyncOutput();

            SysColNames = new List<string>
             {
                 "InsertedDate_DW",
                 "UpdatedDate_DW",
                 "DeletedDate_DW",
                 "RowStatus_DW",
                 "HashKey_DW",
                 "FileDate_DW",
                 "FileName_DW",
                 "FileRowDate_DW",
                 "FileSize_DW",
                 "DataSet_DW",
                 "ValidFrom_DW",
                 "ValidTo_DW"
             };

            InvalidChecksumDataTypes = new List<string>
            {
                "xml",
                "geography",
                "varbinary",
                "hierarchyid",
                "image"
            };

            IgnoreChecksumColumns = new List<string>
            {
                "UpdatedDate_DW",
                "DeletedDate_DW",
                "InsertedDate_DW",
                "RowStatus_DW",
                "HashKey_DW",
                "FileDate_DW",
                "FileName_DW",
                "FileRowDate_DW",
                "FileSize_DW",
                "FileLineNumber_DW",
                "ValidFrom_DW",
                "ValidTo_DW"
            };
        }
    }
}
