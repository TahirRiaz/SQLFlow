using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("IngestionTokenize", Schema = "flw")]
    public partial class IngestionTokenize
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int TokenID { get; set; }

        public int FlowID { get; set; }

        public string ColumnName { get; set; }

        public string TokenExpAlias { get; set; }

    }
}