using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLog", Schema = "flw")]
    public partial class SysLog
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public string ProcessShort { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public int? DurationFlow { get; set; }

        public int? DurationPre { get; set; }

        public int? DurationPost { get; set; }

        public int? Fetched { get; set; }

        public int? Inserted { get; set; }

        public int? Updated { get; set; }

        public int? Deleted { get; set; }

        public bool? Success { get; set; }

        public decimal? FlowRate { get; set; }

        public int? NoOfThreads { get; set; }

        public string SysAlias { get; set; }

        public string Batch { get; set; }

        public string Process { get; set; }

        public string FileName { get; set; }

        public int? FileSize { get; set; }

        public string FileDate { get; set; }

        public string FileDateHist { get; set; }

        public string ExecMode { get; set; }

        public string SelectCmd { get; set; }

        public string InsertCmd { get; set; }

        public string UpdateCmd { get; set; }

        public string DeleteCmd { get; set; }

        public string RuntimeCmd { get; set; }

        public string CreateCmd { get; set; }

        public string SurrogateKeyCmd { get; set; }

        public string ErrorInsert { get; set; }

        public string ErrorUpdate { get; set; }

        public string ErrorDelete { get; set; }

        public string ErrorRuntime { get; set; }

        public string FromObjectDef { get; set; }

        public string ToObjectDef { get; set; }

        public string PreProcessOnTrgDef { get; set; }

        public string PostProcessOnTrgDef { get; set; }

        [Column(TypeName="xml")]
        public string DataTypeWarning { get; set; }

        [Column(TypeName="xml")]
        public string ColumnWarning { get; set; }

        public string AssertRowCount { get; set; }

        public DateTime? NextExportDate { get; set; }

        public int? NextExportValue { get; set; }

        public string WhereIncExp { get; set; }

        public string WhereDateExp { get; set; }

        [Column(TypeName="xml")]
        public string WhereXML { get; set; }

        public string TrgIndexes { get; set; }

    }
}