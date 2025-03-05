using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("ReportFlowHealthCheck", Schema = "flw")]
    public partial class FlowHealthCheck
    {
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]

        [Key]
        public int RowNum { get; set; }
        public int HealthCheckID { get; set; }

        [Required]
        public string HealthCheckName { get; set; }
        

        [Required]
        public string DateColumn { get; set; }

        [Required]
        public string BaseValueExp { get; set; }

        public string FilterCriteria { get; set; }

        public string MLModelName { get; set; }

        public string MLModelSelection { get; set; }

        public DateTime? MLModelDate { get; set; }

        public DateTime? ResultDate { get; set; }

        [Required]
        public int FlowID { get; set; }

        public DateTime? Date { get; set; }

        public string trgObject { get; set; }

        public int? BaseValue { get; set; }

        public int? BaseValueAdjusted { get; set; }

        public int? PredictedValue { get; set; }

        public bool? IsNoData { get; set; }

        public bool? AnomalyDetected { get; set; }
        
        public int? Year { get; set; }

        public int? Quarter { get; set; }

        public int? WeekOfYear { get; set; }

        public int? MonthNumber { get; set; }

        public int? DayOfWeekNumber { get; set; }

        public bool? IsWeekend { get; set; }

        public bool? IsHoliday { get; set; }

    }
}