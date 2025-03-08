using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysServicePrincipal", Schema = "flw")]
    public partial class SysServicePrincipal
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ServicePrincipalID { get; set; }

        [Required]
        public string ServicePrincipalAlias { get; set; }

        public string TenantId { get; set; }

        public string SubscriptionId { get; set; }

        public string ApplicationId { get; set; }

        public string ClientSecret { get; set; }

        public string ResourceGroup { get; set; }

        public string DataFactoryName { get; set; }

        public string AutomationAccountName { get; set; }

        public string StorageAccountName { get; set; }

        public string BlobContainer { get; set; }

        public string KeyVaultName { get; set; }
    }
}