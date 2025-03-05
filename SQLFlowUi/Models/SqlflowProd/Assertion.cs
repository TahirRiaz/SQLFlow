using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("Assertion", Schema = "flw")]
    public partial class Assertion
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int AssertionID { get; set; }

        [Required]
        public string AssertionName { get; set; }

        public string AssertionExp { get; set; }

    }
}