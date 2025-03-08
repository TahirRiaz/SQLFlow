using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDoc", Schema = "flw")]
    public partial class SysDoc
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SysDocID { get; set; }

        public string ObjectName { get; set; }

        public string ObjectType { get; set; }

        public string Description { get; set; }

        public string Summary { get; set; }

        public string Question { get; set; }

        public string Label { get; set; }

        public string AdditionalInfo { get; set; }

        public string ObjectDef { get; set; }

        public string RelationJson { get; set; }

        public string DependsOnJson { get; set; }

        public string DependsOnByJson { get; set; }

        public string DescriptionOld { get; set; }

        public string PayLoadJson { get; set; }

        public DateTime? ScriptDate { get; set; }

        public string PromptDescription { get; set; }

        public string PromptSummary { get; set; }

        public string PromptQuestion { get; set; }

        public long? ScriptGenID { get; set; }
    }
}