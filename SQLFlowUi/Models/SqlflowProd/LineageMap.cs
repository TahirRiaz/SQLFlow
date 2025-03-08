using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("LineageMap", Schema = "flw")]
    public partial class LineageMap
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int LineageParsedID { get; set; }

        [Required]
        public int RecID { get; set; }

        public bool? Virtual { get; set; }

        public string Batch { get; set; }

        public string SysAlias { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string FromObject { get; set; }

        public string ToObject { get; set; }

        public string PathStr { get; set; }

        public string PathNum { get; set; }

        public int? RootObjectMK { get; set; }

        public string RootObject { get; set; }

        public bool? Circular { get; set; }

        public int? Step { get; set; }

        public int? Sequence { get; set; }

        public int? Level { get; set; }

        public int? Priority { get; set; }

        public int? NoOfChildren { get; set; }

        public int? MaxLevel { get; set; }

        public string FromObjectType { get; set; }

        public string ToObjectType { get; set; }

        public bool? SourceIsAzCont { get; set; }

        public int? CommandTimeout { get; set; }

        public int? MaxConcurrency { get; set; }

        public DateTime? LastExec { get; set; }

        public int? Status { get; set; }

        public int? SolidEdge { get; set; }

        public string LatestFileProcessed { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public bool? DataStatus { get; set; }

        public string NextStepFlows { get; set; }

        public string SrcAlias { get; set; }

        public string TrgAlias { get; set; }
    }
}