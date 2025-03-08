using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysBatch", Schema = "flw")]
    public partial class SysBatch
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SysBatchID { get; set; }

        public string Batch { get; set; }

        public int? NoOfThreads { get; set; }
    }
}