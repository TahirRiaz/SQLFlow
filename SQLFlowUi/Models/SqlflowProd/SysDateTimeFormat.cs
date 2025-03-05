using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDateTimeFormat", Schema = "flw")]
    public partial class SysDateTimeFormat
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int FormatID { get; set; }

        public string Format { get; set; }

        [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
        public int? FormatLength { get; set; }

    }
}