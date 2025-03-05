using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysFlowNoteType", Schema = "flw")]
    public partial class SysFlowNoteType
    {
        [Key]
        [Required]
        public string FlowNoteType { get; set; }

    }
}