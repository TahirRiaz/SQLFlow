using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysAlias", Schema = "flw")]
    public partial class SysAlias
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SystemID { get; set; }

        public string System { get; set; }

        [Column("SysAlias")]
        public string SysAlias1 { get; set; }

        public string Description { get; set; }

        public string Owner { get; set; }

        public string DomainExpert { get; set; }
    }
}