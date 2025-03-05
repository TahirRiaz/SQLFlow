using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SurrogateKey", Schema = "flw")]
    public partial class SurrogateKey
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SurrogateKeyID { get; set; }

        public int FlowID { get; set; }

        public string SurrogateServer { get; set; }

        public string SurrogateDbSchTbl { get; set; }

        public string SurrogateColumn { get; set; }

        public string KeyColumns { get; set; }

        public string sKeyColumns { get; set; }

        public string PreProcess { get; set; }

        public string PostProcess { get; set; }

        public int? ToObjectMK { get; set; }

    }
}