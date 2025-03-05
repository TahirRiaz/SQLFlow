using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysHashKeyType", Schema = "flw")]
    public partial class SysHashKeyType
    {
        [Key]
        [Required]
        public string HashKeyType { get; set; }

        [Required]
        public string DataType { get; set; }

        public string DataTypeExp { get; set; }

    }
}