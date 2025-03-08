using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("IngestionVirtual", Schema = "flw")]
    public partial class IngestionVirtual
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int VirtualID { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string ColumnName { get; set; }

        public string DataType { get; set; }

        public string DataTypeExp { get; set; }

        public string SelectExp { get; set; }
    }
}