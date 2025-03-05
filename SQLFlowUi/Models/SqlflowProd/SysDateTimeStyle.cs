using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDateTimeStyle", Schema = "flw")]
    public partial class SysDateTimeStyle
    {
        public int? StyleCode { get; set; }

        public string Query { get; set; }

        public string DateStyle { get; set; }

        public string DateSample { get; set; }

        public string Type { get; set; }

    }
}