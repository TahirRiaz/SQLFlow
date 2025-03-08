using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("IngestionTokenExp", Schema = "flw")]
    public partial class IngestionTokenExp
    {
        [Key]
        [Required]
        public string TokenExpAlias { get; set; }

        public string SelectExp { get; set; }

        public string SelectExpFull { get; set; }

        public string DataType { get; set; }

        public string Description { get; set; }

        public string Example { get; set; }
    }
}