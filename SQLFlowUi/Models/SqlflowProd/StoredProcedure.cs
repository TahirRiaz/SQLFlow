using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("StoredProcedure", Schema = "flw")]
    public partial class StoredProcedure
    {
        [Key]
        [Required]
        public int FlowID { get; set; }

        [Required]
        public string SysAlias { get; set; }

        public string Batch { get; set; }

        [Required]
        public string trgServer { get; set; }

        [Required]
        public string trgDBSchSP { get; set; }

        public bool? OnErrorResume { get; set; }

        public string PostInvokeAlias { get; set; }

        public string Description { get; set; }

        public string FlowType { get; set; }

        public bool? DeactivateFromBatch { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

    }
}