namespace SQLFlowUi.Models.sqlflowProd
{
    public partial class GetApiKey
    {
        public int APIKeyID { get; set; }

        public string ServiceType { get; set; }

        public string ApiKeyAlias { get; set; }

        public string ServicePrincipalAlias { get; set; }

        public string KeyVaultSecretName { get; set; }

    }
}

