using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("GeoCoding", Schema = "flw")]
    public partial class GeoCoding
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int GeoCodingID { get; set; }

        [Required]
        public int FlowID { get; set; }

        [Required]
        public string GoogleAPIKey { get; set; }

        [Required]
        public string KeyColumn { get; set; }

        public string LonColumn { get; set; }

        public string LatColumn { get; set; }

        public string AddressColumn { get; set; }

        [Required]
        public string trgDBSchTbl { get; set; }
    }
}