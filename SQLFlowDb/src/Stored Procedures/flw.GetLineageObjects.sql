SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

--SELECT * FROM  [flw].[ObjectDS]

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetLineageObjects]
  -- Date				:   2022.05.12
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure will generate DS for Data Lineage Calculation
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.05.12		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetLineageObjects]
    @Alias NVARCHAR(70) = NULL, -- FlowID from table [flw].[Ingestion]
    @All INT = 1,
    @dbg INT = 0                -- Debug Level
AS
BEGIN

    --select * FROM [flw].[ObjectDS]     WHERE ObjectMK = 845; 
    IF LEN(ISNULL(@Alias, '')) > 0
    BEGIN
        SELECT MAX([FlowID]) AS [FlowID],
               MAX([UsedAs]) AS [UsedAs],
               MAX([ObjectType]) AS [ObjectType],
               [ObjectMK],
               [ObjectName],
               MAX([Database]) AS [Database],
               MAX([Schema]) AS [Schema],
               MAX([Object]) AS [Object],
               MAX([SourceType]) AS [SourceType],
               MAX(DatabaseName) AS DatabaseName,
               MAX([Alias]) AS [Alias],
               [ConnectionString],
               MAX(srcTenantId) AS srcTenantId,
               MAX([srcSubscriptionId]) AS [srcSubscriptionId],
               MAX([srcApplicationId]) AS [srcApplicationId],
               MAX([srcClientSecret]) AS [srcClientSecret],
               MAX([srcKeyVaultName]) AS [srcKeyVaultName],
               MAX([srcSecretName]) AS [srcSecretName],
               MAX([srcResourceGroup]) AS [srcResourceGroup],
               MAX([srcDataFactoryName]) AS [srcDataFactoryName],
               MAX([srcAutomationAccountName]) AS [srcAutomationAccountName],
               MAX([srcStorageAccountName]) AS [srcStorageAccountName],
               MAX([srcBlobContainer]) AS [srcBlobContainer],
               MAX([DS]) AS [DS],
               MAX([SysAlias]) AS [SysAlias]
        FROM [flw].[ObjectDS]
        WHERE ObjectMK > 0
              AND Alias IN
                  (
                      SELECT Item FROM [flw].[StringSplit](@Alias, ',')
                  )
              AND [NotProcessed] = CASE
                                       WHEN @All = 1 THEN
                                           [NotProcessed]
                                       ELSE
                                           1
                                   END
        --and [ObjectType] = 'sp'
        --AND ObjectMK IN (692)
        --and ObjectName LIKE '%Quest%'
        GROUP BY [ObjectMK],
                 [ObjectName],
                 [ConnectionString]
        ORDER BY DatabaseName;

        --Get DataSubscribers Dependencies
        SELECT [FlowID],
               [FlowType],
               [SubscriberName],
               [ToObjectMK],
               STRING_AGG([FullyQualifiedQuery], ';') AS sqlCmd
        FROM [flw].[DataSubscriberDetails]
        WHERE srcServer IN
              (
                  SELECT Item FROM [flw].[StringSplit](@Alias, ',')
              )
        GROUP BY [FlowID],
                 [FlowType],
                 [SubscriberName],
                 [ToObjectMK];

        --Get DataSet For Relation Parsing
        SELECT [FlowID],
               [FlowType],
               [SubscriberName],
               [srcServer],
               [ToObjectMK],
               MAX([ConnectionString]) AS [ConnectionString],
               MAX([srcTenantId]) AS [srcTenantId],
               MAX([srcSubscriptionId]) AS [srcSubscriptionId],
               MAX([srcApplicationId]) AS [srcApplicationId],
               MAX([srcClientSecret]) AS [srcClientSecret],
               MAX([srcKeyVaultName]) AS [srcKeyVaultName],
               MAX([srcSecretName]) AS [srcSecretName],
               MAX([srcResourceGroup]) AS [srcResourceGroup],
               MAX([srcDataFactoryName]) AS [srcDataFactoryName],
               MAX([srcAutomationAccountName]) AS [srcAutomationAccountName],
               MAX([srcStorageAccountName]) AS [srcStorageAccountName],
               MAX([srcBlobContainer]) AS [srcBlobContainer],
               STRING_AGG([FullyQualifiedQuery], ';') AS sqlCmd
        FROM [flw].[DataSubscriberDetails]
        WHERE srcServer IN
              (
                  SELECT Item FROM [flw].[StringSplit](@Alias, ',')
              )
        GROUP BY [FlowID],
                 [FlowType],
                 [SubscriberName],
                 [srcServer],
                 [ToObjectMK];


    END;
    ELSE
    BEGIN
        SELECT MAX([FlowID]) AS [FlowID],
               MAX([UsedAs]) AS [UsedAs],
               MAX([ObjectType]) AS [ObjectType],
               [ObjectMK],
               [ObjectName],
               MAX([Database]) AS [Database],
               MAX([Schema]) AS [Schema],
               MAX([Object]) AS [Object],
               MAX([SourceType]) AS [SourceType],
               MAX(DatabaseName) AS DatabaseName,
               MAX([Alias]) AS [Alias],
               [ConnectionString],
               MAX(srcTenantId) AS srcTenantId,
               MAX([srcSubscriptionId]) AS [srcSubscriptionId],
               MAX([srcApplicationId]) AS [srcApplicationId],
               MAX([srcClientSecret]) AS [srcClientSecret],
               MAX([srcKeyVaultName]) AS [srcKeyVaultName],
               MAX([srcSecretName]) AS [srcSecretName],
               MAX([srcResourceGroup]) AS [srcResourceGroup],
               MAX([srcDataFactoryName]) AS [srcDataFactoryName],
               MAX([srcAutomationAccountName]) AS [srcAutomationAccountName],
               MAX([srcStorageAccountName]) AS [srcStorageAccountName],
               MAX([srcBlobContainer]) AS [srcBlobContainer],
               MAX([DS]) AS [DS],
               MAX([SysAlias]) AS [SysAlias]
        FROM [flw].[ObjectDS]
        WHERE ISNULL(ObjectMK, 0) >= 0
              AND [NotProcessed] = CASE
                                       WHEN @All = 1 THEN
                                           [NotProcessed]
                                       ELSE
                                           1
                                   END

        --and [ObjectType] = 'sp'
        -- AND ObjectMK IN (692)
        GROUP BY [ObjectMK],
                 [ObjectName],
                 [ConnectionString]
        ORDER BY DatabaseName;

        --Get DataSubscribers Dependencies
        SELECT [FlowID],
               [FlowType],
               [SubscriberName],
               [ToObjectMK],
               STRING_AGG([FullyQualifiedQuery], ';') AS sqlCmd
        FROM [flw].[DataSubscriberDetails]
        GROUP BY [FlowID],
                 [FlowType],
                 [SubscriberName],
                 [ToObjectMK];

        --Get DataSet For Relation Parsing
        SELECT [FlowID],
               [FlowType],
               [SubscriberName],
               [srcServer],
               [ToObjectMK],
               MAX([ConnectionString]) AS [ConnectionString],
               MAX([srcTenantId]) AS [srcTenantId],
               MAX([srcSubscriptionId]) AS [srcSubscriptionId],
               MAX([srcApplicationId]) AS [srcApplicationId],
               MAX([srcClientSecret]) AS [srcClientSecret],
               MAX([srcKeyVaultName]) AS [srcKeyVaultName],
               MAX([srcSecretName]) AS [srcSecretName],
               MAX([srcResourceGroup]) AS [srcResourceGroup],
               MAX([srcDataFactoryName]) AS [srcDataFactoryName],
               MAX([srcAutomationAccountName]) AS [srcAutomationAccountName],
               MAX([srcStorageAccountName]) AS [srcStorageAccountName],
               MAX([srcBlobContainer]) AS [srcBlobContainer],
               STRING_AGG([FullyQualifiedQuery], ';') AS sqlCmd
        FROM [flw].[DataSubscriberDetails]
        GROUP BY [FlowID],
                 [FlowType],
                 [SubscriberName],
                 [srcServer],
                 [ToObjectMK];

    END;
--CASE WHEN LEN(ISNULL(@Alias,'')) = 0 THEN Alias ELSE @Alias END

END;


--SELECT * FROM  [flw].[ObjectDS]
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure will generate DS for Data Lineage Calculation', 'SCHEMA', N'flw', 'PROCEDURE', N'GetLineageObjects', NULL, NULL
GO
