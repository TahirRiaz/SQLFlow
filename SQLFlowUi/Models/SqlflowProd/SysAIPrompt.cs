#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysAIPrompt", Schema = "flw")]
    public partial class SysAIPrompt
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int PromptID { get; set; }

        [Required]   
        public string? PromptName { get; set; }

        public string? ApiKeyAlias { get; set; }
        
        public string? PayLoadJson { get; set; }

    }
}