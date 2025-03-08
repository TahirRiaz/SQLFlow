using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysAPIKey", Schema = "flw")]
    public partial class SysAPIKey
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ApiKeyID { get; set; }

        [Required]
        public string ServiceType { get; set; }

        public string ApiKeyAlias { get; set; }

        public string AccessKey { get; set; }

        public string SecretKey { get; set; }

        public string ServicePrincipalAlias { get; set; }

        public string KeyVaultSecretName { get; set; }
    }
}