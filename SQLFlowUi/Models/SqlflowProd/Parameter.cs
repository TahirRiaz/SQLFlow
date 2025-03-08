using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("Parameter", Schema = "flw")]
    public partial class Parameter
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ParameterID { get; set; }

        [Required]
        public int FlowID { get; set; }

        public string ParamAltServer { get; set; }

        public string ParamName { get; set; }

        [Required]
        public string SelectExp { get; set; }

        public bool? PreFetch { get; set; }

        public string Defaultvalue { get; set; }
    }
}