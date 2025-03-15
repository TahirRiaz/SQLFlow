SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- Stored Procedure

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVIncrementalDS]
  -- Date				:   2022-11-13
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this stored procedure, [flw].[GetRVIncrementalDS], is to retrieve the incremental dataset's metadata for the next data flow in the lineage based on the provided FlowID. 
							It supports various data flow types, including 'ing' (Ingestion) and 'ado' (PreIngestionADO).
							The stored procedure handles the retrieval of target database details, source and target table columns, incremental columns, date column, and other related metadata.
  -- Summary			:	The stored procedure accepts the following input parameters:

							@FlowID: The FlowID of the current data flow
							@DateColumn: The date column for the incremental dataset
							@IncrementalColumns: The incremental columns for the dataset
							@SourceType: The source type of the data
							It retrieves the minimum step value for the next data flow in the lineage after the given FlowID.

							Based on the next FlowType ('ing' or 'ado'), it retrieves the metadata for the target database, schema, and object, along with incremental columns, date column, IncrementalClauseExp, noOfOverlapDays, and other necessary details.

							The stored procedure returns this metadata to be used in the subsequent data flows, which may involve data ingestion or processing based on the acquired metadata.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-11-13		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVIncrementalDS]
    @FlowID INT,
    @DateColumn [NVARCHAR](250) = N'',
    @IncrementalColumns NVARCHAR(1024) = N'',
    @SourceType NVARCHAR(255) = ''
AS
BEGIN

    --Dataset for more accurate incremental load
    DECLARE @Step INT;
    DECLARE @NextFlowID INT;
    DECLARE @NextFlowType VARCHAR(25);
    --Find Next Step
    SELECT @Step = MIN(Step)
    FROM [flw].[LineageAfter](@FlowID, 0)
    WHERE FlowID <> @FlowID;

    SELECT TOP 1
           @NextFlowID = FlowID,
           @NextFlowType = [FlowType]
    FROM [flw].[SysLog]
    WHERE FlowID IN
          (
              SELECT Item
              FROM [flw].[LineageMap]
                  CROSS APPLY
              (SELECT Item FROM [flw].[StringSplit]([NextStepFlows], ',') ) a
              WHERE LEN([NextStepFlows]) > 0
                    AND FlowID IN ( @FlowID )

          --SELECT FlowID
          --         FROM [flw].[LineageAfter](@FlowID, 0)
          --         WHERE FlowID <> @FlowID
          --               AND Step = @Step
          )
    ORDER BY [EndTime] ASC;

    IF (@NextFlowType = 'ing')
    BEGIN
        SELECT i.FlowID nxtFlowID,
               PARSENAME(i.[trgDBSchTbl], 3) AS nxtTrgDatabase,
               PARSENAME(i.[trgDBSchTbl], 2) AS nxtTrgSchema,
               PARSENAME(i.[trgDBSchTbl], 1) AS nxtTrgObject,
               i.IncrementalColumns AS nxtIncrementalColumns,
               i.DateColumn AS nxtDateColumn,
               i.IncrementalClauseExp AS nxtIncrementalClauseExp,
               i.NoOfOverlapDays AS nxtNoOfOverlapDays,
               ds.IsSynapse nxtTrgIsSynapse,
               nxtTenantId = ISNULL(skv1.TenantId, ''),
               nxtSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
               nxtApplicationId = ISNULL(skv1.ApplicationId, ''),
               nxtClientSecret = ISNULL(skv1.ClientSecret, ''),
               nxtKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
               nxtSecretName = ISNULL(ds.KeyVaultSecretName, ''),
               nxtResourceGroup = ISNULL(skv1.ResourceGroup, ''),
               nxtDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
               nxtAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
               nxtStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
               nxtBlobContainer = ISNULL(skv1.BlobContainer, ''),
               ds.ConnectionString AS nxtConnectionString,
               ds.SourceType AS nxtSourceType,
               CASE
                   WHEN ds.IsSynapse = 1 THEN
                       'nolock'
                   ELSE
                       'readpast'
               END nxtTrgWithHint,
               @DateColumn AS DateColumn,
               @IncrementalColumns AS IncrementalColumns,
               @SourceType AS srcDSType
        FROM flw.Ingestion AS i
            INNER JOIN flw.SysDataSource AS ds
                ON i.trgServer = ds.Alias
            LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
                ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
        WHERE (i.FlowID = @NextFlowID);
    END;
    ELSE IF (@NextFlowType = 'ado')
    BEGIN
        SELECT i.FlowID nxtFlowID,
               PARSENAME(i.[trgDBSchTbl], 3) AS nxtTrgDatabase,
               PARSENAME(i.[trgDBSchTbl], 2) AS nxtTrgSchema,
               PARSENAME(i.[trgDBSchTbl], 1) AS nxtTrgObject,
               i.IncrementalColumns AS nxtIncrementalColumns,
               i.DateColumn AS nxtDateColumn,
               i.IncrementalClauseExp AS nxtIncrementalClauseExp,
               i.NoOfOverlapDays AS nxtNoOfOverlapDays,
               ds.IsSynapse nxtTrgIsSynapse,
               nxtTenantId = ISNULL(skv1.TenantId, ''),
               nxtSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
               nxtApplicationId = ISNULL(skv1.ApplicationId, ''),
               nxtClientSecret = ISNULL(skv1.ClientSecret, ''),
               nxtKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
               nxtSecretName = ISNULL(ds.KeyVaultSecretName, ''),
               nxtResourceGroup = ISNULL(skv1.ResourceGroup, ''),
               nxtDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
               nxtAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
               nxtStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
               nxtBlobContainer = ISNULL(skv1.BlobContainer, ''),
               ds.ConnectionString AS nxtConnectionString,
               ds.SourceType AS nxtSourceType,
               CASE
                   WHEN ds.IsSynapse = 1 THEN
                       'nolock'
                   ELSE
                       'readpast'
               END nxtTrgWithHint,
               @DateColumn AS DateColumn,
               @IncrementalColumns AS IncrementalColumns,
               @SourceType AS srcDSType
        FROM [flw].[PreIngestionADO] AS i
            INNER JOIN flw.SysDataSource AS ds
                ON i.trgServer = ds.Alias
            LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
                ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
        WHERE (i.FlowID = @NextFlowID);
    END;
    ELSE
    BEGIN
        SELECT i.FlowID nxtFlowID,
               PARSENAME(i.[trgDBSchTbl], 3) AS nxtTrgDatabase,
               PARSENAME(i.[trgDBSchTbl], 2) AS nxtTrgSchema,
               PARSENAME(i.[trgDBSchTbl], 1) AS nxtTrgObject,
               i.IncrementalColumns AS nxtIncrementalColumns,
               i.DateColumn AS nxtDateColumn,
               i.IncrementalClauseExp AS nxtIncrementalClauseExp,
               i.NoOfOverlapDays AS nxtNoOfOverlapDays,
               ds.IsSynapse nxtTrgIsSynapse,
               nxtTenantId = ISNULL(skv1.TenantId, ''),
               nxtSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
               nxtApplicationId = ISNULL(skv1.ApplicationId, ''),
               nxtClientSecret = ISNULL(skv1.ClientSecret, ''),
               nxtKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
               nxtSecretName = ISNULL(ds.KeyVaultSecretName, ''),
               nxtResourceGroup = ISNULL(skv1.ResourceGroup, ''),
               nxtDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
               nxtAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
               nxtStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
               nxtBlobContainer = ISNULL(skv1.BlobContainer, ''),
               ds.ConnectionString AS nxtConnectionString,
               ds.SourceType AS nxtSourceType,
               CASE
                   WHEN ds.IsSynapse = 1 THEN
                       'nolock'
                   ELSE
                       'readpast'
               END nxtTrgWithHint,
               @DateColumn AS DateColumn,
               @IncrementalColumns AS IncrementalColumns,
               @SourceType AS srcDSType
        FROM flw.Ingestion AS i
            INNER JOIN flw.SysDataSource AS ds
                ON i.trgServer = ds.Alias
            LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
                ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
        WHERE 1 = 0;
    END;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this stored procedure, [flw].[GetRVIncrementalDS], is to retrieve the incremental dataset''s metadata for the next data flow in the lineage based on the provided FlowID. 
							It supports various data flow types, including ''ing'' (Ingestion) and ''ado'' (PreIngestionADO).
							The stored procedure handles the retrieval of target database details, source and target table columns, incremental columns, date column, and other related metadata.
  -- Summary			:	The stored procedure accepts the following input parameters:

							@FlowID: The FlowID of the current data flow
							@DateColumn: The date column for the incremental dataset
							@IncrementalColumns: The incremental columns for the dataset
							@SourceType: The source type of the data
							It retrieves the minimum step value for the next data flow in the lineage after the given FlowID.

							Based on the next FlowType (''ing'' or ''ado''), it retrieves the metadata for the target database, schema, and object, along with incremental columns, date column, IncrementalClauseExp, noOfOverlapDays, and other necessary details.

							The stored procedure returns this metadata to be used in the subsequent data flows, which may involve data ingestion or processing based on the acquired metadata.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVIncrementalDS', NULL, NULL
GO
