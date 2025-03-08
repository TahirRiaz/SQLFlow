using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogFile", Schema = "flw")]
    public partial class SysLogFile
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int LogFileID { get; set; }

        public string BatchID { get; set; }

        public int FlowID { get; set; }

        public string FileDate_DW { get; set; }

        public string FileName_DW { get; set; }

        public DateTime? FileRowDate_DW { get; set; }

        public decimal? FileSize_DW { get; set; }

        public int? FileColumnCount { get; set; }

        public int? ExpectedColumnCount { get; set; }

        public string DataSet_DW { get; set; }
    }
}