using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysCompressionType", Schema = "flw")]
    public partial class SysCompressionType
    {
        [Key]
        [Required]
        public string CompressionType { get; set; }

    }
}