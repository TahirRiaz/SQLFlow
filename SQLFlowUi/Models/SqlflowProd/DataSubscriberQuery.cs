using System;
using System.Collections.Generic;
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

        public string srcServer { get; set; }

        public string QueryName { get; set; }

        public string FullyQualifiedQuery { get; set; }

        public DataSubscriber DataSubscriber { get; set; }



    }
}