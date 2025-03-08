using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysStats", Schema = "flw")]
    public partial class SysStats
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int StatsID { get; set; }

        public string FlowType { get; set; }

        public DateTime? StatsDate { get; set; }

        [Required]
        public int FlowID { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public int? DurationFlow { get; set; }

        public int? DurationPre { get; set; }

        public int? DurationPost { get; set; }

        public int? Fetched { get; set; }

        public int? Inserted { get; set; }

        public int? Updated { get; set; }

        public int? Deleted { get; set; }

        public bool? Success { get; set; }

        public decimal? FlowRate { get; set; }

        public int? NoOfThreads { get; set; }

        public string ExecMode { get; set; }

        public string FileName { get; set; }

        public int? FileSize { get; set; }

        public string FileDate { get; set; }
    }
}