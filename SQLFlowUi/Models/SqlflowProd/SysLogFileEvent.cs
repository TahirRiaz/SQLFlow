using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogFileEvent", Schema = "flw")]
    public partial class SysLogFileEvent
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int LogFileEventID { get; set; }

        public int? FlowID { get; set; }

        public string FileName_DW { get; set; }

        public DateTime? EventDate_DW { get; set; }
    }
}