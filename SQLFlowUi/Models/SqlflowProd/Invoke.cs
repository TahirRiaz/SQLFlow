using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("Invoke", Schema = "flw")]
    public partial class Invoke
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        public string SysAlias { get; set; }

        public string trgServicePrincipalAlias { get; set; }

        public string srcServicePrincipalAlias { get; set; }
        
        [Required]
        public string InvokeAlias { get; set; }

        public string InvokeType { get; set; }

        public string InvokePath { get; set; }

        public string InvokeFile { get; set; }

        public string Code { get; set; }

        public string Arguments { get; set; }

        public string PipelineName { get; set; }

        public string RunbookName { get; set; }

        public string ParameterJSON { get; set; }

        public string Batch { get; set; }

        public bool? OnErrorResume { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public int? ToObjectMK { get; set; }
        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

    }
}