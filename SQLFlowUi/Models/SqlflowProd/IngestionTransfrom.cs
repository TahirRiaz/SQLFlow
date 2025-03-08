using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("IngestionTransfrom", Schema = "flw")]
    public partial class IngestionTransfrom
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int TransfromID { get; set; }

        public int FlowID { get; set; }

        public string ColumnName { get; set; }

        public string DataTypeExp { get; set; }

        public string SelectExp { get; set; }
    }
}