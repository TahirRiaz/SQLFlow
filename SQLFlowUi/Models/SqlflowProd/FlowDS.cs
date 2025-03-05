using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("FlowDS", Schema = "flw")]
    public partial class FlowDS
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public string SysAlias { get; set; }
        
        public string Batch { get; set; }

        public string trgServer { get; set; }
        
        public string trgDBSchObj { get; set; }

        public string trgDatabase { get; set; }

        public string trgSchema { get; set; }

        public string trgObject { get; set; }

        public string SourceType { get; set; }

        public string DatabaseName { get; set; }

        public string Alias { get; set; }

        public int? IsSynapse { get; set; }

        public int? IsLocal { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public int DS { get; set; }

        public string preFilter { get; set; }

        public string Process { get; set; }

        public string ProcessShort { get; set; }

        public bool? DeactivateFromBatch { get; set; }

       

    }
}