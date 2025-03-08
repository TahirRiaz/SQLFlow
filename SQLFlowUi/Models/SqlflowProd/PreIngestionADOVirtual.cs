using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("PreIngestionADOVirtual", Schema = "flw")]
    public partial class PreIngestionADOVirtual
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int VirtualID { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string ColumnName { get; set; }

        public string DataType { get; set; }

        public int? Length { get; set; }

        public int? Precision { get; set; }

        public int? Scale { get; set; }

        public string SelectExp { get; set; }
    }
}