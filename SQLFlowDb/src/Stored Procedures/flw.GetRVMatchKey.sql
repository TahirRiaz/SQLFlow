SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVMatchKey]
  -- Date				:   2024.06.27
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.11.12		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVMatchKey]
    @Batch NVARCHAR(255) = '',
    @FlowID INT = 0,                  -- FlowID from [flw].[Ingestion]
    @MatchKeyID INT = 0,
    @ExecMode VARCHAR(50) = 'Manual', -- Manual, Batch, Initial, Schedule
    @dbg INT = 1                      -- Show details. returns true/false if not

AS
BEGIN

    SET NOCOUNT ON;

    --Please see the table [flw].[SysCFG] for an explanation for each of these paramters
    DECLARE @BulkLoadTimeoutInSek INT = [flw].[GetCFGParamVal]('BulkLoadTimeoutInSek');
    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');
    DECLARE @BulkLoadBatchSize INT = [flw].[GetCFGParamVal]('BulkLoadBatchSize');
    DECLARE @NoOfThreads INT = [flw].[GetCFGParamVal]('NoOfThreads');
    DECLARE @MaxRetry INT = [flw].[GetCFGParamVal]('MaxRetry');
    DECLARE @RetryDelayMS INT = [flw].[GetCFGParamVal]('RetryDelayMS');
    DECLARE @stgSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema01Raw');
    DECLARE @delSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema14Mkey');
    DECLARE @ColCleanupSQLRegExp NVARCHAR(255) = [flw].[GetCFGParamVal]('ColCleanupSQLRegExp');

    --Lets find the relevant [MatchKeyID] from Flowid

    IF (@FlowID > 0)
    BEGIN

        SELECT @MatchKeyID = [MatchKeyID]
        FROM
        (
            SELECT TOP 1
                   [MatchKeyID]
            FROM [flw].[MatchKey]
            WHERE FlowID = @FlowID
            UNION
            SELECT TOP 1
                   [MatchKeyID]
            FROM [flw].[MatchKey]
            WHERE [trgDBSchTbl] IN
                  (
                      SELECT [trgDBSchObj] FROM [flw].[FlowDS] WHERE FlowID = @FlowID
                  )
        ) a;
    END;



    IF @MatchKeyID IS NULL
        SET @MatchKeyID = 0;

    DECLARE @MatchKeyID2 INT,
            @FlowID2 INT;
    DECLARE @srcConnectionString [NVARCHAR](250),
            @trgConnectionString [NVARCHAR](250),
            @trgDBSchTbl [NVARCHAR](250),
            @DateColumn [NVARCHAR](250),
            @IgnoreDeletedRowsAfter [INT],
            @srcDatabase [NVARCHAR](250) = N'',
            @srcSchema [NVARCHAR](250) = N'',
            @srcObject [NVARCHAR](255) = N'',
            @trgDatabase [NVARCHAR](255) = N'',
            @trgSchema [NVARCHAR](250) = N'',
            @trgObject [NVARCHAR](250) = N'',
            @PreProcessOnTrg [NVARCHAR](250) = N'',
            @PostProcessOnTrg [NVARCHAR](250) = N'',
            @srcFilter NVARCHAR(1024),
            @trgFilter NVARCHAR(1024),
            @SysAlias [NVARCHAR](250) = N'',
            @srcIsSynapse BIT = 0,
            @trgIsSynapse BIT = 0,
            @FlowType [NVARCHAR](250) = N'',
            @OnErrorResume BIT = 1,
            @ActionType NVARCHAR(20) = N'',
            @srcTenantId [NVARCHAR](100) = N'',
            @srcSubscriptionId [NVARCHAR](100) = N'',
            @srcApplicationId [NVARCHAR](100) = N'',
            @srcClientSecret [NVARCHAR](100) = N'',
            @srcKeyVaultName [NVARCHAR](250) = N'',
            @srcSecretName [NVARCHAR](250) = N'',
            @srcResourceGroup [NVARCHAR](250) = N'',
            @srcDataFactoryName [NVARCHAR](250) = N'',
            @srcAutomationAccountName [NVARCHAR](250) = N'',
            @srcStorageAccountName [NVARCHAR](250) = N'',
            @srcBlobContainer [NVARCHAR](250) = N'',
            @trgTenantId [NVARCHAR](100) = N'',
            @trgSubscriptionId [NVARCHAR](100) = N'',
            @trgApplicationId [NVARCHAR](100) = N'',
            @trgClientSecret [NVARCHAR](100) = N'',
            @trgKeyVaultName [NVARCHAR](250) = N'',
            @trgSecretName [NVARCHAR](250) = N'',
            @trgResourceGroup [NVARCHAR](250) = N'',
            @trgDataFactoryName [NVARCHAR](250) = N'',
            @trgAutomationAccountName [NVARCHAR](250) = N'',
            @trgStorageAccountName [NVARCHAR](250) = N'',
            @trgBlobContainer [NVARCHAR](250) = N'',
            @KeyColumns NVARCHAR(1024) = N'',
            @srcDSType NVARCHAR(1024) = N'';

    SELECT TOP 1
           @MatchKeyID2 = MatchKeyID,
           @FlowID2 = b.FlowID,
           @srcConnectionString = srcBDS.ConnectionString,
           @srcDatabase = [flw].[StrCleanup](srcDatabase),
           @srcSchema = [flw].[StrCleanup](srcSchema),
           @srcObject = [flw].[StrCleanup](srcObject),
           @trgConnectionString = trgBDS.ConnectionString,
           @trgDBSchTbl = ISNULL([flw].[StrCleanup](flw.GetValidSrcTrgName(b.trgDBSchTbl)),[flw].[StrCleanup](flw.GetValidSrcTrgName(i.trgDBSchTbl))),
           @DateColumn = COALESCE([flw].[StrCleanup](b.DateColumn), [flw].[StrCleanup](i.DateColumn), ''),
           @IgnoreDeletedRowsAfter = ISNULL(IgnoreDeletedRowsAfter, 0),
           @PreProcessOnTrg = ISNULL(LTRIM(RTRIM(b.PreProcessOnTrg)), ''),
           @PostProcessOnTrg = ISNULL(LTRIM(RTRIM(b.PostProcessOnTrg)), ''),
           @srcFilter = ISNULL([flw].[StrCleanup](b.srcFilter), ''),
           @KeyColumns = COALESCE([flw].[StrCleanup](b.KeyColumns), [flw].[StrCleanup](i.KeyColumns), ''),
           @SysAlias = ISNULL(b.SysAlias, ''),
           @srcIsSynapse = ISNULL(srcBDS.IsSynapse, 0),
           @trgIsSynapse = ISNULL(trgBDS.IsSynapse, 0),
           @SysAlias = ISNULL(b.SysAlias, ''),
           @Batch = ISNULL(b.Batch, ''),
           @OnErrorResume = ISNULL(b.OnErrorResume, 1),
           @Batch = ISNULL(b.Batch, ''),
           @srcTenantId = COALESCE(skv1.TenantId, skv3.TenantId, ''),
           @srcSubscriptionId = COALESCE(skv1.SubscriptionId, skv3.SubscriptionId, ''),
           @srcApplicationId = COALESCE(skv1.ApplicationId, skv3.ApplicationId, ''),
           @srcClientSecret = COALESCE(skv1.ClientSecret, skv3.ClientSecret, ''),
           @srcKeyVaultName = COALESCE(skv1.[KeyVaultName], skv3.[KeyVaultName], ''),
           @srcSecretName = COALESCE(srcBDS.KeyVaultSecretName, srcBDS2.KeyVaultSecretName, ''),
           @srcResourceGroup = COALESCE(skv1.ResourceGroup, skv3.ResourceGroup, ''),
           @srcDataFactoryName = COALESCE(skv1.DataFactoryName, skv3.DataFactoryName, ''),
           @srcAutomationAccountName = COALESCE(skv1.AutomationAccountName, skv3.AutomationAccountName, ''),
           @srcStorageAccountName = COALESCE(skv1.StorageAccountName, skv3.StorageAccountName, ''),
           @srcBlobContainer = COALESCE(skv1.BlobContainer, skv3.BlobContainer, ''),
           @trgTenantId = COALESCE(skv2.TenantId, skv4.TenantId, ''),
           @trgSubscriptionId = COALESCE(skv2.SubscriptionId, skv4.SubscriptionId, ''),
           @trgApplicationId = COALESCE(skv2.ApplicationId, skv4.ApplicationId, ''),
           @trgClientSecret = COALESCE(skv2.ClientSecret, skv4.ClientSecret, ''),
           @trgKeyVaultName = COALESCE(skv2.KeyVaultName, skv4.KeyVaultName, ''),
           @trgKeyVaultName = COALESCE(skv2.[KeyVaultName], skv4.[KeyVaultName], ''),
           @trgSecretName = COALESCE(trgBDS.KeyVaultSecretName, trgBDS2.KeyVaultSecretName, ''),
           @trgResourceGroup = COALESCE(skv2.ResourceGroup, skv4.ResourceGroup, ''),
           @trgDataFactoryName = COALESCE(skv2.DataFactoryName, skv4.DataFactoryName, ''),
           @trgAutomationAccountName = COALESCE(skv2.AutomationAccountName, skv4.AutomationAccountName, ''),
           @trgStorageAccountName = COALESCE(skv2.StorageAccountName, skv4.StorageAccountName, ''),
           @trgBlobContainer = COALESCE(skv2.BlobContainer, skv4.BlobContainer, ''),
           @srcDSType = srcBDS.SourceType,
           @trgFilter = ISNULL([flw].[StrCleanup](trgFilter), ''),
           @ActionType = ISNULL(b.ActionType, '')
    FROM [flw].[MatchKey] b
        INNER JOIN flw.SysDataSource srcBDS
            ON srcBDS.Alias = b.srcServer
        INNER JOIN flw.SysDataSource trgBDS
            ON trgBDS.Alias = b.trgServer
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON srcBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON trgBDS.ServicePrincipalAlias = skv2.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[Ingestion] i
            ON i.FlowID = b.FlowID
        LEFT OUTER JOIN flw.SysDataSource srcBDS2
            ON srcBDS2.Alias = b.srcServer
        LEFT OUTER JOIN flw.SysDataSource trgBDS2
            ON trgBDS2.Alias = b.trgServer
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv3
            ON srcBDS2.ServicePrincipalAlias = skv3.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv4
            ON trgBDS2.ServicePrincipalAlias = skv4.ServicePrincipalAlias
    WHERE [MatchKeyID] = CASE
                             WHEN @MatchKeyID = 0 THEN
                                 [MatchKeyID]
                             ELSE
                                 @MatchKeyID
                         END
          AND b.Batch = CASE
                          WHEN LEN(@Batch) > 2 THEN
                              @Batch
                          ELSE
                              b.Batch
                      END;
    SELECT @trgDatabase = [1],
           @trgSchema = [2],
           @trgObject = [3]
    FROM
    (
        SELECT Ordinal,
               Item
        FROM flw.StringSplit(flw.GetValidSrcTrgName(@trgDBSchTbl), '.')
    ) AS SourceTable
    PIVOT
    (
        MAX(Item)
        FOR Ordinal IN ([1], [2], [3])
    ) AS PivotTable;

    DECLARE @rValue NVARCHAR(MAX);
    DECLARE @cmd VARCHAR(8000);
    DECLARE @c VARCHAR(25) = CHAR(39);


    IF (@srcConnectionString IS NOT NULL)
    BEGIN
        DECLARE @slutt DATETIME;
        DECLARE @start DATETIME = GETDATE();

        SELECT @MatchKeyID2 AS MatchKeyID,
               @FlowID2 AS FlowID,
               @srcConnectionString AS SrcConString,
               @trgConnectionString AS TrgConString,
               @srcDatabase AS srcDatabase,
               @srcSchema AS srcSchema,
               @srcObject AS srcObject,
               @stgSchema AS stgSchema,
               @trgDatabase AS trgDatabase,
               @trgSchema AS trgSchema,
               @trgObject AS trgObject,
               @DateColumn AS DateColumn,
               @IgnoreDeletedRowsAfter AS IgnoreDeletedRowsAfter,
               @BulkLoadTimeoutInSek AS BulkLoadTimeoutInSek,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @BulkLoadBatchSize AS BulkLoadBatchSize,
               @MaxRetry AS MaxRetry,
               @RetryDelayMS AS RetryDelayMS,
               @PreProcessOnTrg AS PreProcessOnTrg,
               @PostProcessOnTrg AS PostProcessOnTrg,
               @ColCleanupSQLRegExp AS ColCleanupSQLRegExp,
               @srcFilter AS srcFilter,
               @trgFilter AS trgFilter,
               @KeyColumns AS KeyColumns,
               @SysAlias AS SysAlias,
               @dbg AS dbg,
               @srcIsSynapse AS srcIsSynapse,
               @trgIsSynapse AS trgIsSynapse,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @srcDSType AS srcDSType,
               @delSchema AS delSchema,
               @srcTenantId AS srcTenantId,
               @srcSubscriptionId AS srcSubscriptionId,
               @srcApplicationId AS srcApplicationId,
               @srcClientSecret AS srcClientSecret,
               @srcKeyVaultName AS srcKeyVaultName,
               @srcSecretName AS srcSecretName,
               @srcResourceGroup AS srcResourceGroup,
               @srcDataFactoryName AS srcDataFactoryName,
               @srcAutomationAccountName AS srcAutomationAccountName,
               @srcStorageAccountName AS srcStorageAccountName,
               @srcBlobContainer AS srcBlobContainer,
               @trgTenantId AS trgTenantId,
               @trgSubscriptionId AS trgSubscriptionId,
               @trgApplicationId AS trgApplicationId,
               @trgClientSecret AS trgClientSecret,
               @trgKeyVaultName AS trgKeyVaultName,
               @trgSecretName AS trgSecretName,
               @trgResourceGroup AS trgResourceGroup,
               @trgDataFactoryName AS trgDataFactoryName,
               @trgAutomationAccountName AS trgAutomationAccountName,
               @trgStorageAccountName AS trgStorageAccountName,
               @trgBlobContainer AS trgBlobContainer,
               @ActionType AS ActionType;
    END;

END;
GO
