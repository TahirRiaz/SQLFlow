#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("DataSubscriberQuery", Schema = "flw")]
    public partial class DataSubscriberQuery
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int QueryID { get; set; }

        [Required]
        public int FlowID { get; set; }

        [Required]
        public  string QueryName { get; set; }

        [Required]
        public string FullyQualifiedQuery { get; set; }

        [Required]
        public string? srcServer { get; set; }

        public DataSubscriber DataSubscriber { get; set; }

        


    }
}