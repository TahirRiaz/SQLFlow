using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SQLFlowUi.Models.sqlflowProd
{
    [Table("SysDataSource", Schema = "flw")]
    public partial class SysDataSource
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int DataSourceID { get; set; }

        public string SourceType { get; set; }

        public string DatabaseName { get; set; }

        public string Alias { get; set; }

        public string ConnectionString { get; set; }

        public string ServicePrincipalAlias { get; set; }

        public string KeyVaultSecretName { get; set; }

        public bool? SupportsCrossDBRef { get; set; }

        public bool? IsSynapse { get; set; }

        public bool? IsLocal { get; set; }

        public bool? ActivityMonitoring { get; set; }
    }
}