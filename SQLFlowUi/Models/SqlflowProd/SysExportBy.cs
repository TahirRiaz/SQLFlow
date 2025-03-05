using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysExportBy", Schema = "flw")]
    public partial class SysExportBy
    {
        [Key]
        [Required]
        public string ExportBy { get; set; }

        [Required]
        public string ExportByName { get; set; }

    }
}