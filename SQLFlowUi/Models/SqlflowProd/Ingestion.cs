using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("Ingestion", Schema = "flw")]
    public partial class Ingestion
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        [Required]
        public string SysAlias { get; set; }

        [Required]
        public string srcServer { get; set; }

        [Required]
        public string srcDBSchTbl { get; set; }

        [Required]
        public string trgServer { get; set; }

        [Required]
        public string trgDBSchTbl { get; set; }

        public string Batch { get; set; }

        public int? BatchOrderBy { get; set; }

        public bool? MatchKeysInSrcTrg { get; set; }

        public string IgnoreColumnsInHashkey { get; set; }
        
        public bool? DeactivateFromBatch { get; set; }

        public bool? StreamData { get; set; }

        public int? NoOfThreads { get; set; }

        public bool? UseBatchUpsertToAvoideLockEscalation { get; set; }

        public int? BatchUpsertRowCount { get; set; }

        public string KeyColumns { get; set; }

        public string IncrementalColumns { get; set; }

        public string IncrementalClauseExp { get; set; }

        public string DateColumn { get; set; }

        public string DataSetColumn { get; set; }

        public int? NoOfOverlapDays { get; set; }

        public bool? FetchMinValuesFromSrc { get; set; }

        public bool? SkipUpdateExsisting { get; set; }

        public bool? SkipInsertNew { get; set; }

        public int? FullLoad { get; set; }

        public bool? TruncateTrg { get; set; }

        public bool? TruncatePreTableOnCompletion { get; set; }

        public string srcFilter { get; set; }

        public bool? srcFilterIsAppend { get; set; }

        public string IdentityColumn { get; set; }

        public string HashKeyColumns { get; set; }

        public string HashKeyType { get; set; }

        public string IgnoreColumns { get; set; }

        public string SysColumns { get; set; }

        public bool? ColumnStoreIndexOnTrg { get; set; }

        public bool? SyncSchema { get; set; }

        public bool? OnErrorResume { get; set; }

        public bool? InitLoad { get; set; }

        public DateTime? InitLoadFromDate { get; set; }

        public DateTime? InitLoadToDate { get; set; }

        public string InitLoadBatchBy { get; set; }

        public int? InitLoadBatchSize { get; set; }

        public string InitLoadKeyColumn { get; set; }

        public int? InitLoadKeyMaxValue { get; set; }

        public string ReplaceInvalidCharsWith { get; set; }


        public bool? OnSyncCleanColumnName { get; set; }

        public bool? OnSyncConvertUnicodeDataType { get; set; }

        

        public string CleanColumnNameSQLRegExp { get; set; }

        public bool? trgVersioning { get; set; }

        public bool? InsertUnknownDimRow { get; set; }

        public bool? TokenVersioning { get; set; }

        public int? TokenRetentionDays { get; set; }

        public string PreProcessOnTrg { get; set; }

        public string PostProcessOnTrg { get; set; }

        public string PreInvokeAlias { get; set; }

        public string PostInvokeAlias { get; set; }

        public string FlowType { get; set; }

        public string Description { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        public string Assertions { get; set; }

        public string trgDesiredIndex { get; set; }
    }
}