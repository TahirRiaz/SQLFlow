using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("HealthCheck", Schema = "flw")]
    public partial class HealthCheck
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int HealthCheckID { get; set; }

        [Required]
        public int FlowID { get; set; }

        [Required]
        public string HealthCheckName { get; set; }

        public string DBSchTbl { get; set; }

        [Required]
        public string DateColumn { get; set; }

        [Required]
        public string BaseValueExp { get; set; }

        public string FilterCriteria { get; set; }

        public int MLMaxExperimentTimeInSeconds { get; set; }
         

        public string MLModelSelection { get; set; }
        
        public string MLModelName { get; set; }

        public string Result { get; set; }
        
        public byte[] MLModel { get; set; }
        
    }
}