using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDataSubscriberType", Schema = "flw")]
    public partial class SysDataSubscriberType
    {
        //[DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Key] 
        [Required] 
        public string SubscriberType { get; set; }

    }
}