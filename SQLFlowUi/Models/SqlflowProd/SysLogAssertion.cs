using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogAssertion", Schema = "flw")]
    public partial class SysLogAssertion
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        public int? FlowID { get; set; }

        public int? AssertionID { get; set; }

        public DateTime? AssertionDate { get; set; }

        public string AssertionSqlCmd { get; set; }

        public string Result { get; set; }

        public string AssertedValue { get; set; }

        public string TraceLog { get; set; }
    }
}