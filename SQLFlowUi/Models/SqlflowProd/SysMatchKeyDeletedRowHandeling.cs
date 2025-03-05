using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysMatchKeyDeletedRowHandeling", Schema = "flw")]
    public partial class SysMatchKeyDeletedRowHandeling
    {
        //[DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Key] 
        [Required]
        public string ActionType { get; set; }

        public string ActionTypeDescription { get; set; }

    }
}