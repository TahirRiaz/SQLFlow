using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("MatchKey", Schema = "flw")]
    public partial class MatchKey
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int MatchKeyID { get; set; }

        public int FlowID { get; set; }

        public string Batch { get; set; }

        [Required]
        public string SysAlias { get; set; }

        [Required]
        public string srcServer { get; set; }

        [Required]
        public string srcDatabase { get; set; }

        public string srcSchema { get; set; }

        [Required]
        public string srcObject { get; set; }

        [Required]
        public string trgServer { get; set; }

        [Required]
        public string trgDBSchTbl { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public string KeyColumns { get; set; }

        public string DateColumn { get; set; }

        public string ActionType { get; set; }

        public int? ActionThresholdPercent { get; set; }

        public int? IgnoreDeletedRowsAfter { get; set; }

        public string srcFilter { get; set; }

        public string trgFilter { get; set; }

        public bool? OnErrorResume { get; set; }

        public string PreProcessOnTrg { get; set; }

        public string PostProcessOnTrg { get; set; }

        public string Description { get; set; }

        public int? ToObjectMK { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }
    }
}