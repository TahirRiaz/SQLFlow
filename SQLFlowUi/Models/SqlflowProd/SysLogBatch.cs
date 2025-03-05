using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogBatch", Schema = "flw")]
    public partial class SysLogBatch
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        public string BatchID { get; set; }

        public string Batch { get; set; }

        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public string SysAlias { get; set; }

        public int? Step { get; set; }

        public int? Sequence { get; set; }

        public int? dbg { get; set; }

        public string Status { get; set; }

        public DateTime? BatchTime { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public bool? SourceIsAzCont { get; set; }

    }
}