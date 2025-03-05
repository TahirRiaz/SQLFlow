using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogExport", Schema = "flw")]
    public partial class SysLogExport
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        public string BatchID { get; set; }

        public int FlowID { get; set; }

        public string SqlCMD { get; set; }

        public string WhereClause { get; set; }

        public string FilePath_DW { get; set; }

        public string FileName_DW { get; set; }

        public decimal? FileSize_DW { get; set; }

        public int? FileRows_DW { get; set; }

        public DateTime? NextExportDate { get; set; }

        public int? NextExportValue { get; set; }

        public DateTime? ExportDate { get; set; }

    }
}