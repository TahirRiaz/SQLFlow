using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("PreIngestionPRC", Schema = "flw")]
    public partial class PreIngestionPRC
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        public string SysAlias { get; set; }

        public string ServicePrincipalAlias { get; set; }

        public string Batch { get; set; }

        [Required]
        public string srcServer { get; set; }

        public string srcPath { get; set; }

        public string srcFile { get; set; }

        public string srcCode { get; set; }

        [Required]
        public string trgServer { get; set; }

        public string trgDBSchTbl { get; set; }

        public string preFilter { get; set; }

        public bool? SyncSchema { get; set; }

        public int? ExpectedColumnCount { get; set; }

        public bool? FetchDataTypes { get; set; }

        public bool? OnErrorResume { get; set; }

        public int? NoOfThreads { get; set; }

        public string PreProcessOnTrg { get; set; }

        public string PostProcessOnTrg { get; set; }

        public string PreInvokeAlias { get; set; }

        public int? BatchOrderBy { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public string FlowType { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public bool? ShowPathWithFileName { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        public string trgDesiredIndex { get; set; }

    }
}