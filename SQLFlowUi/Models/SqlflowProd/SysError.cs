using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysError", Schema = "flw")]
    public partial class SysError
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ErrorID { get; set; }

        public string ErrorMessage { get; set; }

        public string Solution { get; set; }

    }
}