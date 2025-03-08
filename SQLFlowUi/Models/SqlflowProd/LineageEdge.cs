using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("LineageEdge", Schema = "flw")]
    public partial class LineageEdge
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RecID { get; set; }

        [Required]
        public int DataSet { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string FlowType { get; set; }

        public int? FromObjectMK { get; set; }

        public int? ToObjectMK { get; set; }

        public string FromObject { get; set; }

        public string ToObject { get; set; }

        public string Dependency { get; set; }

        public bool? IsAfterDependency { get; set; }

        public bool? Circular { get; set; }

        public DateTime? CreateDate { get; set; }
    }
}