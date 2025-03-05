using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysCFG", Schema = "flw")]
    public partial class SysCFG
    {
        [Key]
        [Required]
        public string ParamName { get; set; }

        public string ParamValue { get; set; }

        public string ParamJsonValue { get; set; }
        
        public string Description { get; set; }

    }
}