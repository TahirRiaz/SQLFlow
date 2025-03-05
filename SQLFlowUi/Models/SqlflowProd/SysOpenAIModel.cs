using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysOpenAIModel", Schema = "flw")]
    public partial class SysOpenAIModel
    {
        [Key]
        [Required]
        public string Model { get; set; }
        public int MaxTokens { get; set; }
        public int MinTokens { get; set; }
    }
}