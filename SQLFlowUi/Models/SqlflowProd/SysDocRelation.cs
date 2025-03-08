using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDocRelation", Schema = "flw")]
    public partial class SysDocRelation
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int RelationID { get; set; }

        public string LeftObject { get; set; }

        public string LeftObjectCol { get; set; }

        public string RightObject { get; set; }

        public string RightObjectCol { get; set; }

        public bool? ManualEntry { get; set; }
    }
}