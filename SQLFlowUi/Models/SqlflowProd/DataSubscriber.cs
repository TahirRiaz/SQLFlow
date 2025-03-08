using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("DataSubscriber", Schema = "flw")]
    public partial class DataSubscriber
    {
        [Key]
        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public string SubscriberType { get; set; }

        public string SubscriberName { get; set; }

        public string Batch { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedDate { get; set; }

        public ICollection<DataSubscriberQuery> DataSubscriberQuery { get; set; }
    }
}