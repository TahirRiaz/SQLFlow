using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysFileEncoding", Schema = "flw")]
    public partial class SysFileEncoding
    {
        [Key]
        [Required]
        public string Encoding { get; set; }

        [Required]
        public string EncodingName { get; set; }

    }
}