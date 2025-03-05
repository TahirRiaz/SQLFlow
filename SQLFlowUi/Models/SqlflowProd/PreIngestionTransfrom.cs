using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("PreIngestionTransfrom", Schema = "flw")]
    public partial class PreIngestionTransfrom
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int TransfromID { get; set; }

        [Required]
        public int FlowID { get; set; }

        [Required]
        public string FlowType { get; set; }

        public bool? Virtual { get; set; }

        [Required]
        public string ColName { get; set; }

        public string SelectExp { get; set; }

        public string ColAlias { get; set; }

        public string DataType { get; set; }

        public int? SortOrder { get; set; }

        public bool? ExcludeColFromView { get; set; }

    }
}