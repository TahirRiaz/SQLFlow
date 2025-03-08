using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysLogMatchKey", Schema = "flw")]
    public partial class SysLogMatchKey
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("SysLogMatchKey")]
        public int SysLogMatchKey1 { get; set; }

        [Required]
        public int MatchKeyID { get; set; }

        [Required]
        public int FlowID { get; set; }

        public int? BatchID { get; set; }

        public string SysAlias { get; set; }

        public string Batch { get; set; }

        public DateTime? StartTime { get; set; }

        public DateTime? EndTime { get; set; }

        public bool? Status { get; set; }

        public int? DurationMatch { get; set; }

        public int? DurationPre { get; set; }

        public int? DurationPost { get; set; }

        public int? SrcRowCount { get; set; }

        public int? SrcDelRowCount { get; set; }

        public int? TrgRowCount { get; set; }

        public int? TrgDelRowCount { get; set; }

        public int? TaggedRowCount { get; set; }

        public string ErrorMessage { get; set; }

        public string TraceLog { get; set; }
    }
}