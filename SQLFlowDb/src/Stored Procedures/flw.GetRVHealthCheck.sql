SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO





/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVHealthCheck]
  -- Date				:   2024.01.10
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024.01.10		Initial
  ##################################################################################################################################################
*/


CREATE PROCEDURE [flw].[GetRVHealthCheck] @FlowID INT -- FlowID from [flw].[Ingestion]
AS
BEGIN


    SET NOCOUNT ON;


    SELECT skey.[HealthCheckID],
           i.FlowID,
           ISNULL(skey.[DBSchTbl], i.trgDBSchTbl) AS trgDBSchTbl,
           ISNULL(skey.[DateColumn], '') AS [DateColumn],
           ISNULL(skey.BaseValueExp, '') AS BaseValueExp,
           ISNULL(skey.FilterCriteria, '') AS [FilterCriteria],
           ISNULL(skey.[MLMaxExperimentTimeInSeconds], 120) AS [MLMaxExperimentTimeInSeconds],
           ISNULL(skey.MLModelSelection, '') AS [MLModelSelection],
           ISNULL(skey.MLModelName, '') AS [MLModelName],
		   [MLModel] AS [MLModel],
           'SELECT ' + ISNULL(skey.[DateColumn], '') + ' AS [Date], CAST(' + ISNULL(skey.BaseValueExp, '')
           + ' as float) AS BaseValue ' + ' FROM ' + ISNULL(skey.[DBSchTbl], i.trgDBSchTbl) + ' WHERE 1 = 1 '
           + CASE
                 WHEN LEN(ISNULL(skey.FilterCriteria, '')) > 0 THEN
                     ISNULL(skey.FilterCriteria, '')
                 ELSE
                     ''
             END + ' GROUP BY ' + skey.[DateColumn] AS hcCmd,
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
    FROM [flw].[HealthCheck] skey
        LEFT OUTER JOIN flw.Ingestion AS i
            ON i.FlowID = skey.FlowID
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE i.FlowID = CASE
                         WHEN @FlowID = 0 THEN
                             i.FlowID
                         ELSE
                             @FlowID
                     END;

END;
GO
