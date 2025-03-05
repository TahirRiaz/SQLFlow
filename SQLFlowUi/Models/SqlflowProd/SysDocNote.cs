using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDocNote", Schema = "flw")]
    public partial class SysDocNote
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int DocNoteID { get; set; }

        [Required]
        public string ObjectName { get; set; }

       

        public string Title { get; set; }

        public string Description { get; set; }

       
        

        public DateTime? Created { get; set; }

        public string CreatedBy { get; set; }

    }
}