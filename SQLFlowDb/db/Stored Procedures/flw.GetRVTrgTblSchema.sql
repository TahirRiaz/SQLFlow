SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE PROCEDURE [flw].[GetRVTrgTblSchema] @FlowID INT -- FlowID from [flw].[Ingestion]
AS
BEGIN


    SET NOCOUNT ON;


    
	
	SELECT i.FlowID,
           ISNULL(i.trgDBSchObj, '') AS trgDBSchObj,
           ISNULL(trgBDS.ConnectionString, '') AS trgConString,
		   trgTenantId = ISNULL(skv1.TenantId, ''),
           trgSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           trgApplicationId = ISNULL(skv1.ApplicationId, ''),
           trgClientSecret = ISNULL(skv1.ClientSecret, ''),
           ISNULL(skv1.KeyVaultName, '') AS trgKeyVaultName,
           ISNULL(trgBDS.KeyVaultSecretName, '') AS trgSecretName,
           trgResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           trgDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           trgAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           trgStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           trgBlobContainer = ISNULL(skv1.BlobContainer, ''),

           ISNULL(trgBDS.IsSynapse, 0) AS IsSynapse
    FROM [flw].[FlowDS] AS i
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE i.FlowID = CASE
                         WHEN @FlowID = 0 THEN
                             i.FlowID
                         ELSE
                             @FlowID
                     END
    ORDER BY i.FlowID;

END;
GO
