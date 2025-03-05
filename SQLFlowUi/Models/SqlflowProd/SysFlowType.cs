using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysFlowType", Schema = "flw")]
    public partial class SysFlowType
    {
        [Key]
        [Required]
        public string FlowType { get; set; }
        
        public string Description { get; set; }

        public bool HasPreIngestionTransform { get; set; }
        
    }
}