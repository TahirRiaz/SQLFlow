SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- Stored Procedure

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVPrevDS]
  -- Date				:   2024-05-20
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
							
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024-05-20		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVPrevDS] @FlowID INT
AS
BEGIN

    --Dataset for more accurate incremental load
    DECLARE @Step INT;
    DECLARE @PrevFlowID INT;
    DECLARE @PrevFlowType VARCHAR(25);
    --Find Prev Step
    SELECT @Step = MIN(Step)
    FROM [flw].[LineageAfter](@FlowID, 0)
    WHERE FlowID <> @FlowID;

    SELECT TOP 1
           @PrevFlowID = FlowID,
           @PrevFlowType = [FlowType]
    FROM [flw].[SysLog]
    WHERE FlowID IN
          (
              SELECT FlowID
              FROM [flw].[LineageMap]
                  CROSS APPLY
              (SELECT Item FROM [flw].[StringSplit]([NextStepFlows], ',') ) a
              WHERE LEN([NextStepFlows]) > 0
                    AND Item IN ( @FlowID )
          )
          AND FlowType IN
              (
                  SELECT FlowType FROM [flw].[SysFlowType] WHERE SrcIsFile = 1
              )
    ORDER BY [EndTime] ASC;

    SELECT i.FlowID prvFlowID,
           [trgDatabase] AS prvTrgDatabase,
           [trgSchema] AS prvTrgSchema,
           [trgObject] AS prvTrgObject,
           ds.IsSynapse prvTrgIsSynapse,
           prvTenantId = ISNULL(skv1.TenantId, ''),
           prvSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           prvApplicationId = ISNULL(skv1.ApplicationId, ''),
           prvClientSecret = ISNULL(skv1.ClientSecret, ''),
           prvKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
           prvSecretName = ISNULL(ds.KeyVaultSecretName, ''),
           prvResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           prvDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           prvAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           prvStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           prvBlobContainer = ISNULL(skv1.BlobContainer, ''),
           ds.ConnectionString AS prvConnectionString,
           ds.SourceType AS prvSourceType,
           CASE
               WHEN ds.IsSynapse = 1 THEN
                   'nolock'
               ELSE
                   'readpast'
           END prvTrgWithHint
    FROM [flw].[FlowDS] AS i
        INNER JOIN flw.SysDataSource AS ds
            ON i.trgServer = ds.Alias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE (i.FlowID = @PrevFlowID);


END;
GO
