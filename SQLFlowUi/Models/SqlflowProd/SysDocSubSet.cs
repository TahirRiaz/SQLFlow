#nullable enable
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDocSubSet", Schema = "flw")]
    public partial class SysDocSubSet
    {
        [Key]
        public int SysDocSubsetID { get; set; }
        public string ObjectName { get; set; }
        public string ObjectType { get; set; }
        public string? Label { get; set; }
        public string? Question { get; set; }

        // Navigation property to SysDoc
        
    }
}
