using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysFlowNote", Schema = "flw")]
    public partial class SysFlowNote
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int FlowNoteID { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string FlowNoteType { get; set; }

        public string Title { get; set; }

        public string Description { get; set; }

        public bool? Resolved { get; set; }

        public DateTime? Created { get; set; }

        public string CreatedBy { get; set; }
    }
}