using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysSourceControl", Schema = "flw")]
    public partial class SysSourceControl
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SourceControlID { get; set; }

        public string Batch { get; set; }

        [Required]
        public string SCAlias { get; set; }

        [Required]
        public string Server { get; set; }

        [Required]
        public string DBName { get; set; }

        public string RepoName { get; set; }

        public string ScriptToPath { get; set; }

        public string ScriptDataForTables { get; set; }
    }
}