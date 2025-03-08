using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysColumn", Schema = "flw")]
    public partial class SysColumn
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SysColumnID { get; set; }

        public string ColumnName { get; set; }

        public string DataType { get; set; }

        public string DataTypeExp { get; set; }

        public string SelectExp { get; set; }
    }
}