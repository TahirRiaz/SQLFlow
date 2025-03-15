SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO






/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVAssertion]
  -- Date				:   2024.01.14
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024.01.14		Initial
  ##################################################################################################################################################
*/


CREATE PROCEDURE [flw].[GetRVAssertion] @FlowID INT -- FlowID from [flw].[Ingestion]
AS
BEGIN


    SET NOCOUNT ON;

    ;WITH BASE
    AS (SELECT i.*,
               Item
        FROM flw.Ingestion i
            CROSS APPLY
        (SELECT Item FROM [flw].[StringSplit]([Assertions], ',') ) a )
    SELECT i.FlowID,
           asr.AssertionID,
		   asr.AssertionName,
           ISNULL(skey.[DBSchTbl], i.trgDBSchTbl) AS trgDBSchTbl,
           COALESCE(skey.FilterCriteria, i.[IncrementalClauseExp], '') AS [FilterCriteria],
           REPLACE(
                      REPLACE([AssertionExp], '@TableName', ISNULL(skey.[DBSchTbl], i.trgDBSchTbl)),
                      '@FilterCriteria',
                      COALESCE(skey.FilterCriteria, i.[IncrementalClauseExp], '')
                  ) AS AssertionSqlCmd,
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
    FROM BASE i
        INNER JOIN [flw].[Assertion] AS asr
            ON asr.[AssertionName] = i.Item
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        LEFT OUTER JOIN [flw].[HealthCheck] AS skey
            ON i.FlowID = skey.FlowID
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
