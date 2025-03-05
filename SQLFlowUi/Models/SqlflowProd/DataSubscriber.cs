using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("DataSubscriber", Schema = "flw")]
    public partial class DataSubscriber
    {
        //[DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Key] [Required] public int FlowID { get; set; } = 0;
       
        [Required]
        public string SubscriberName { get; set; }

        public string SubscriberType { get; set; }
        
        public string FlowType { get; set; } = "sub";


        public string CreatedBy { get; set; }
        public DateTime? CreatedDate { get; set; } = DateTime.Now;

        
        

        public ICollection<DataSubscriberQuery> DataSubscriberQuery { get; set; }

    }
}