#nullable enable
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysSourceControlType", Schema = "flw")]
    public partial class SysSourceControlType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SourceControlTypeID { get; set; }

        [Required]
        public string SourceControlType { get; set; }


        public string? ServicePrincipalAlias { get; set; }
        public string? AccessTokenSecretName { get; set; }
        public string? ConsumerSecretName { get; set; }

        public string? SCAlias { get; set; }

        public string? Username { get; set; }

        public string? AccessToken { get; set; }

        public string? ConsumerKey { get; set; }

        public string? ConsumerSecret { get; set; }

        public string? WorkSpaceName { get; set; }

        public string? ProjectName { get; set; }

        public string? ProjectKey { get; set; }

        public bool? CreateWrkProjRepo { get; set; }

    }
}