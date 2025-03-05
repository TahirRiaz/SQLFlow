using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysSubFolderPattern", Schema = "flw")]
    public partial class SysSubFolderPattern
    {
        [Key]
        [Required]
        public string SubFolderPattern { get; set; }

        [Required]
        public string SubFolderPatternName { get; set; }

    }
}