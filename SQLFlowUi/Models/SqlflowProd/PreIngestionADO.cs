using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("PreIngestionADO", Schema = "flw")]
    public partial class PreIngestionADO
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        [Required]
        public string SysAlias { get; set; }

        [Required]
        public string srcServer { get; set; }

        [Required]
        public string srcDatabase { get; set; }

        public string srcSchema { get; set; }

        [Required]
        public string srcObject { get; set; }

        [Required]
        public string trgServer { get; set; }

        [Required]
        public string trgDBSchTbl { get; set; }

        public string Batch { get; set; }

        public int? BatchOrderBy { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public bool? StreamData { get; set; }

        public int? NoOfThreads { get; set; }

        public string IncrementalColumns { get; set; }

        public string IncrementalClauseExp { get; set; }

        public string DateColumn { get; set; }

        public int? NoOfOverlapDays { get; set; }

        public bool? FetchMinValuesFromSysLog { get; set; }

        public bool? FullLoad { get; set; }

        public bool? TruncateTrg { get; set; }

        public string srcFilter { get; set; }

        public bool? srcFilterIsAppend { get; set; }

        public string preFilter { get; set; }

        public string IgnoreColumns { get; set; }

        public bool? InitLoad { get; set; }

        public DateTime? InitLoadFromDate { get; set; }

        public DateTime? InitLoadToDate { get; set; }

        public string InitLoadBatchBy { get; set; }

        public int? InitLoadBatchSize { get; set; }

        public string InitLoadKeyColumn { get; set; }

        public int? InitLoadKeyMaxValue { get; set; }

        public bool? SyncSchema { get; set; }

        public bool? OnErrorResume { get; set; }

        public string CleanColumnNameSQLRegExp { get; set; }

        public string RemoveInColumnName { get; set; }

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

        public string trgDesiredIndex { get; set; }
        

    }
}