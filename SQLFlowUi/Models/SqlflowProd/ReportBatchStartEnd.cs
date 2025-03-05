using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("ReportBatchStepDetails", Schema = "flw")]
    public partial class ReportBatchStartEnd
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        public string Batch { get; set; }

        public int? Step { get; set; }

        public string ProcessShort { get; set; }


        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public bool Success { get; set; }

        public int? Duration { get; set; }

    }
}