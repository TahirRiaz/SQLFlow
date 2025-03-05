using System.Collections.Generic;

namespace SQLFlowCore.Services.Schema
{
    /// <summary>
    /// Represents the output of a synchronization process in SQLFlowCore.
    /// </summary>
    /// <remarks>
    /// This class contains various properties that hold the state and results of a synchronization process.
    /// These include lists of invalid checksum data types, ignored checksum columns, system column names, 
    /// source and target columns, key columns, incremental columns, ignored columns, hash key columns, 
    /// and a mapping between source and target. It also contains various settings and flags related to the synchronization process.
    /// </remarks>
    internal class SyncOutput
    {
        internal string SrcColumns { get; set; }
        internal string SrcColumnsWithSrc { get; set; }
        internal string SrcColumnsExp { get; set; }

        internal string SrcCleanColumns { get; set; }
        internal string SrcCleanColumnsWithSrc { get; set; }
        internal string SrcCleanColumnsExp { get; set; }

        internal string SrcVirtualCols { get; set; }
        internal string SrcVirtualColsExp { get; set; }

        internal string TrgColumns { get; set; }
        internal string TrgColumnsWithTrg { get; set; }
        internal string TrgColumnsWithSrc { get; set; }

        internal bool TrgExists { get; set; }
        internal string CreateCmd { get; set; }

        internal string SelectColumns { get; set; }

        internal string UpdateColumnsSrcTrg { get; set; }

        internal string CheckSumColumnsSrc { get; set; }
        internal string CheckSumColumnsTrg { get; set; }

        internal string srcType { get; set; }

        internal List<string> KeyColumnList { get; set; }
        internal List<string> IncrementalColumnList { get; set; }
        internal List<string> IgnoreColumnList { get; set; }
        internal List<string> HashKeyColumnList { get; set; }

        internal bool HashKeyColumnFound { get; set; }
        internal bool SyncSchema { get; set; }
        internal bool CleanColumnName { get; set; }
        internal bool ConvUnicodeDt { get; set; }
        internal bool CreateIndexes { get; set; }
        internal string ColCleanupSqlRegExp { get; set; }
        internal int CommandTimeOutInSeconds { get; set; }
        internal Dictionary<string, string> SrcTrgMapping { get; set; }
        internal string ValidUpdateColumns { get; set; }
        internal string ValidChkSumColumns { get; set; }
        internal string ValidKeyColumns { get; set; }

        internal bool ImageDataTypeFound { get; set; } = false;
        internal string incrementalColumnsQuoted { get; set; }
        internal string keyColumnsQuoted { get; set; }
        internal string dateColumnQuoted { get; set; }
        internal string dataSetColumnQuoted { get; set; }
        internal string srcDatabaseQuoted { get; set; }
        internal string srcSchemaQuoted { get; set; }
        internal string srcObjectQuoted { get; set; }
        internal string stgSchemaQuoted { get; set; }
        internal string trgDatabaseQuoted { get; set; }
        internal string trgSchemaQuoted { get; set; }
        internal string trgObjectQuoted { get; set; }
        internal string HashKeyColumnsQuoted { get; set; }
        internal string identityColumnQuoted { get; set; }
        internal string IgnoreColumnsInHashkeyQuoted { get; set; }


    }
}
