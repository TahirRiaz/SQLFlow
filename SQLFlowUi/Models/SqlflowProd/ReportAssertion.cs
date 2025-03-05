using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("ReportAssertion", Schema = "flw")]
    public partial class ReportAssertion
    {
        [Key]
        public int RecID { get; set; }

        public int? FlowID { get; set; }

        [Required]
        public string AssertionName { get; set; }

        public int? AssertionID { get; set; }

        public DateTime? AssertionDate { get; set; }

        public string AssertionExp { get; set; }

        public string AssertionSqlCmd { get; set; }

        public string Result { get; set; }

        public string AssertedValue { get; set; }
        

        public string TraceLog { get; set; }

    }
}