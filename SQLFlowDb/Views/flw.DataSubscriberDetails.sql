SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[DataSubscriberDetails]
AS
SELECT d.FlowID,
       d.FlowType,
       d.SubscriberName,
       d.Batch,
	   dq.srcServer,
       s.ConnectionString AS ConnectionString,
       srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       d.ToObjectMK,
       d.CreatedBy,
       d.CreatedDate,
       dq.FullyQualifiedQuery
FROM flw.DataSubscriber AS d
    INNER JOIN flw.DataSubscriberQuery AS dq
        ON d.FlowID = dq.FlowID
    INNER JOIN [flw].[SysDataSource] s
        ON dq.srcServer = s.Alias
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias;
GO
