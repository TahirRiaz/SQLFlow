using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("LineageObjectRelation", Schema = "flw")]
    public partial class LineageObjectRelation
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ObjectRelationID { get; set; }

        public string LeftObject { get; set; }

        public string LeftObjectCol { get; set; }

        public string RightObject { get; set; }

        public string RightObjectCol { get; set; }

        public bool? ManualEntry { get; set; }
    }
}