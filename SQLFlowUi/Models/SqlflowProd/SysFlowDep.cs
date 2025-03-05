using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysFlowDep", Schema = "flw")]
    public partial class SysFlowDep
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        [Required]
        public int FlowID { get; set; }

        [Required]
        public string FlowType { get; set; }

        public int? Step { get; set; }

        public int? DepFlowID { get; set; }

        public int? DepFlowIDStep { get; set; }

        public string ExecDep { get; set; }

    }
}