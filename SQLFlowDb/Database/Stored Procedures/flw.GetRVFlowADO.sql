SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO





/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVFlowADO]
  -- Date				:   2022.11.12
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to build a runtime dataset for ADO Dataflows
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.11.12		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVFlowADO]
    @FlowID INT,                      -- FlowID from [flw].[Ingestion]
    @ExecMode VARCHAR(50) = 'Manual', -- Manual, Batch, Initial, Schedule
    @dbg INT = 1,                     -- Show details. returns true/false if not
    @AutoCreateSchema INT = 1
AS
BEGIN


    SET NOCOUNT ON;

    DECLARE @curObjName NVARCHAR(255)
        = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name'),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);

    --Ensure that debug info is only printed when called directly.
    IF (@@NESTLEVEL > 1 AND @dbg = 1)
        SET @dbg = 0;

    --Please see the table [flw].[SysCFG] for an explanation for each of these paramters
    DECLARE @BulkLoadTimeoutInSek INT = [flw].[GetCFGParamVal]('BulkLoadTimeoutInSek');
    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');
    DECLARE @BulkLoadBatchSize INT = [flw].[GetCFGParamVal]('BulkLoadBatchSize');
    DECLARE @NoOfThreads INT = [flw].[GetCFGParamVal]('NoOfThreads');
    DECLARE @MaxRetry INT = [flw].[GetCFGParamVal]('MaxRetry');
    DECLARE @RetryDelayMS INT = [flw].[GetCFGParamVal]('RetryDelayMS');
    DECLARE @stgSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema01Raw');
    DECLARE @tmpSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema02Temp');
    DECLARE @ColCleanupSQLRegExp NVARCHAR(255) = [flw].[GetCFGParamVal]('ColCleanupSQLRegExp');


    DECLARE @srcConnectionString [NVARCHAR](250),
            @trgConnectionString [NVARCHAR](250),
            @trgDBSchTbl [NVARCHAR](250),
            @SyncSchema [BIT],
            @OnSyncCleanColumnName [BIT],
            @OnSyncConvertUnicodeDataType [BIT],
            @OnSyncPreserveData [BIT],
            @DateColumn [NVARCHAR](250),
            @NoOfOverlapDays [INT],
            @srcDatabase [NVARCHAR](250) = N'',
            @srcSchema [NVARCHAR](250) = N'',
            @srcObject [NVARCHAR](255) = N'',
            @trgDatabase [NVARCHAR](255) = N'',
            @trgSchema [NVARCHAR](250) = N'',
            @trgObject [NVARCHAR](250) = N'',
            @StreamData BIT = 1,
            @PreProcessOnTrg [NVARCHAR](250) = N'',
            @PostProcessOnTrg [NVARCHAR](250) = N'',
            @IgnoreColumns NVARCHAR(1024) = N'',
            @srcFilter NVARCHAR(1024),
            @SysAlias [NVARCHAR](250) = N'',
            @DbgFileName NVARCHAR(255) = N'',
            @cmdSchema NVARCHAR(MAX) = N'',
            @Fullload BIT = 0,
            @truncateTrg BIT = 0,
            @srcIsSynapse BIT = 0,
            @trgIsSynapse BIT = 0,
            @FlowType [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @IncrementalColumns NVARCHAR(1024) = N'',
            @WhereIncExp NVARCHAR(MAX) = N'',
            @WhereDateExp NVARCHAR(MAX) = N'',
            @WhereXML NVARCHAR(MAX) = N'',
            
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

            @IncrementalClauseExp NVARCHAR(1024) = N'',
            @CleanColumnNameSQLRegExp NVARCHAR(1024) = N'',
            @preFilter NVARCHAR(1024) = N'',
            @srcDSType NVARCHAR(1024) = N'',
            @RemoveInColumnName NVARCHAR(1024) = N'',
            @InitLoad BIT = 1,
            @InitLoadFromDate NVARCHAR(12), --date = GETDATE(),
            @InitLoadToDate NVARCHAR(12),   --date = GETDATE(),
            @InitLoadBatchBy VARCHAR(1) = 'M',
            @InitLoadBatchSize INT,
            @InitLoadKeyMaxValue INT,
            @srcFilterIsAppend BIT = 1,
            @FetchMinValuesFromSysLog BIT = 0,
            @InitLoadKeyColumn NVARCHAR(255),
            @TrgIndexes NVARCHAR(MAX),

			@trgDesiredIndex NVARCHAR(MAX);


    SELECT TOP (1)
           @WhereIncExp = CASE
                              WHEN LEN(ISNULL([IncrementalColumns], '')) > 0 THEN
                                  ISNULL(WhereIncExp, '')
                              ELSE
                                  ''
                          END,
           @WhereDateExp = CASE
                               WHEN LEN(ISNULL([DateColumn], '')) > 0 THEN
                                   ISNULL(WhereDateExp, '')
                               ELSE
                                   ''
                           END
    FROM [flw].[SysLog] l WITH (READPAST)
        INNER JOIN [flw].[PreIngestionADO] b
            ON l.FlowID = b.FlowID
    WHERE l.FlowID = @FlowID;

    SELECT TOP 1
           @srcConnectionString = srcBDS.ConnectionString,
           @srcDatabase = [flw].[StrCleanup](srcDatabase),
           @srcSchema = [flw].[StrCleanup](srcSchema),
           @srcObject = [flw].[StrCleanup](srcObject),
           @trgConnectionString = trgBDS.ConnectionString,
           @trgDBSchTbl = [flw].[StrCleanup](flw.GetValidSrcTrgName(trgDBSchTbl)),
           @SyncSchema = ISNULL(SyncSchema, 0),
           @DateColumn = ISNULL([flw].[StrCleanup](DateColumn), ''),
           @NoOfOverlapDays = ISNULL(NoOfOverlapDays, 0),
           @StreamData = ISNULL(StreamData, 0),
           @PreProcessOnTrg = ISNULL(LTRIM(RTRIM(PreProcessOnTrg)), ''),
           @PostProcessOnTrg = ISNULL(LTRIM(RTRIM(PostProcessOnTrg)), ''),
           @IgnoreColumns = ISNULL([flw].[ListCleanup]([flw].[StrCleanup](IgnoreColumns)), ''),
           @srcFilter = ISNULL([flw].[StrCleanup](srcFilter), ''),
           @preFilter = ISNULL([flw].[StrCleanup](preFilter), ''),
           @SysAlias = ISNULL(b.SysAlias, ''),
           @Fullload = ISNULL(FullLoad, 0),
           @srcIsSynapse = ISNULL(srcBDS.IsSynapse, 0),
           @trgIsSynapse = ISNULL(trgBDS.IsSynapse, 0),
           @truncateTrg = ISNULL(b.TruncateTrg, 0),
           @IncrementalColumns = ISNULL([flw].[ListCleanup]([flw].[StrCleanup](b.IncrementalColumns)), ''),
           @SysAlias = ISNULL(b.SysAlias, ''),
           @Batch = ISNULL(b.Batch, ''),
           @FlowType = ISNULL(b.FlowType, 'ing'),
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @NoOfThreads = CASE
                              WHEN ISNULL(b.NoOfThreads, 0) = 0 THEN
                                  @NoOfThreads
                              ELSE
                                  b.NoOfThreads
                          END,
           @Batch = ISNULL(b.Batch, ''),

		   @srcTenantId = ISNULL(skv1.TenantId, ''),
           @srcSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           @srcApplicationId = ISNULL(skv1.ApplicationId, ''),
           @srcClientSecret = ISNULL(skv1.ClientSecret, ''),
           @srcKeyVaultName = ISNULL(skv1.[KeyVaultName], ''),
           @srcSecretName = ISNULL(srcBDS.KeyVaultSecretName, ''),
           @srcResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           @srcDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           @srcAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           @srcStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           @srcBlobContainer = ISNULL(skv1.BlobContainer, ''),

		   @trgTenantId = ISNULL(skv2.TenantId, ''),
           @trgSubscriptionId = ISNULL(skv2.SubscriptionId, ''),
           @trgApplicationId = ISNULL(skv2.ApplicationId, ''),
           @trgClientSecret = ISNULL(skv2.ClientSecret, ''),
           @trgKeyVaultName = ISNULL(skv2.KeyVaultName, ''),
           @trgKeyVaultName = ISNULL(skv2.[KeyVaultName], ''),
           @trgSecretName = ISNULL(trgBDS.KeyVaultSecretName, ''),
           @trgResourceGroup = ISNULL(skv2.ResourceGroup, ''),
           @trgDataFactoryName = ISNULL(skv2.DataFactoryName, ''),
           @trgAutomationAccountName = ISNULL(skv2.AutomationAccountName, ''),
           @trgStorageAccountName = ISNULL(skv2.StorageAccountName, ''),
           @trgBlobContainer = ISNULL(skv2.BlobContainer, ''),


           @IncrementalClauseExp = ISNULL(b.IncrementalClauseExp, ''),
           @ColCleanupSQLRegExp = COALESCE(CleanColumnNameSQLRegExp, @ColCleanupSQLRegExp, ''),
           @srcDSType = srcBDS.SourceType,
           @RemoveInColumnName = ISNULL(RemoveInColumnName, ''),
           @InitLoad = ISNULL(InitLoad, 0),
           @InitLoadFromDate = CONVERT(VARCHAR, ISNULL(InitLoadFromDate, DATEADD(YEAR, -3, GETDATE())), 23),
           @InitLoadToDate = CONVERT(VARCHAR, ISNULL(InitLoadToDate, GETDATE()), 23),
           @InitLoadBatchBy = ISNULL(InitLoadBatchBy, 'M'),
           @InitLoadBatchSize = ISNULL(InitLoadBatchSize, 1),
           @InitLoadKeyMaxValue = ISNULL(InitLoadKeyMaxValue, 10000000),
           @InitLoadKeyColumn = ISNULL(InitLoadKeyColumn, ''),
           @srcFilterIsAppend = ISNULL(srcFilterIsAppend, 1),
           @FetchMinValuesFromSysLog = ISNULL(FetchMinValuesFromSysLog, 0),
           @TrgIndexes = CASE
                             WHEN LEN(TrgIndexes) > 0 THEN
                                 TrgIndexes
                             ELSE
                                 ''
                         END,
			@trgDesiredIndex = CASE
                             WHEN LEN(trgDesiredIndex) > 0 THEN
                                 trgDesiredIndex
                             ELSE
                                 ''
                         END
			
    FROM [flw].[PreIngestionADO] b
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON b.FlowID = pLog.[FlowID]
        INNER JOIN flw.SysDataSource srcBDS
            ON srcBDS.Alias = b.srcServer
        INNER JOIN flw.SysDataSource trgBDS
            ON trgBDS.Alias = b.trgServer
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON srcBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON trgBDS.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE b.FlowID = @FlowID;

    SELECT TOP (1)
           @WhereIncExp = ISNULL(WhereIncExp, ''),
           @WhereDateExp = ISNULL(WhereDateExp, ''),
           @WhereXML = ISNULL(CAST(WhereXML AS NVARCHAR(MAX)), '')
    FROM [flw].[SysLog]
    WHERE FlowID = @FlowID;

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


    --Set debug filename
    SET @DbgFileName
        = N'FLW_' + [flw].[StrRemRegex](flw.GetValidSrcTrgName(@trgDBSchTbl), '%[^a-z]%') + N'_'
          + CAST(@FlowID AS VARCHAR(255)) + N'.txt';


    IF (@AutoCreateSchema = 1)
    BEGIN

        DECLARE @Init BIT = 1,
                @FirstExec BIT = 1;


        SELECT @Init = [ParamValue]
        FROM flw.SysCFG
        WHERE [ParamName] = 'CreateFlowSchema';

        IF (ISNULL(
            (
                SELECT COUNT([FlowID])
                FROM flw.SysLog
                WHERE [EndTime] IS NULL
                      AND [FlowID] = @FlowID
            ),
            0
                  ) = 0
           )
        BEGIN
            SET @FirstExec = 0;
        END;

        SELECT @cmdSchema = [flw].[GetFlowSchemaScript](@FlowID, @trgDatabase, @trgSchema, @Init, @FirstExec);

        --print @SchemaRes
        SET @curCode
            = N'PRINT [flw].[GetFlowSchemaScript](' + CAST(@FlowID AS VARCHAR(255)) + N',''' + @trgDatabase + N''','''
              + @trgSchema + N''',' + CAST(@Init AS VARCHAR(2)) + N',' + CAST(@FirstExec AS VARCHAR(2)) + N')';

        

    --print @SchemaRes
    END;

    IF (@srcConnectionString IS NOT NULL)
    BEGIN
        DECLARE @slutt DATETIME;
        DECLARE @start DATETIME = GETDATE();

        SELECT @srcConnectionString AS SrcConString,
               @trgConnectionString AS TrgConString,
               @srcDatabase AS srcDatabase,
               @srcSchema AS srcSchema,
               @srcObject AS srcObject,
               @stgSchema AS stgSchema,
               @trgDatabase AS trgDatabase,
               @trgSchema AS trgSchema,
               @trgObject AS trgObject,
               @DateColumn AS DateColumn,
               @SyncSchema AS SyncSchema,
               @OnSyncCleanColumnName AS OnSyncCleanColumnName,
               @OnSyncConvertUnicodeDataType AS OnSyncConvertUnicodeDataType,
               @OnSyncPreserveData AS OnSyncPreserveData,
               @StreamData AS StreamData,
               @NoOfOverlapDays AS NoOfOverlapDays,
               @BulkLoadTimeoutInSek AS BulkLoadTimeoutInSek,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @BulkLoadBatchSize AS BulkLoadBatchSize,
               @MaxRetry AS MaxRetry,
               @RetryDelayMS AS RetryDelayMS,
               @PreProcessOnTrg AS PreProcessOnTrg,
               @PostProcessOnTrg AS PostProcessOnTrg,
               @ColCleanupSQLRegExp AS ColCleanupSQLRegExp,
               @IgnoreColumns AS IgnoreColumns,
               @srcFilter AS srcFilter,
               @preFilter AS preFilter,
               @FlowID AS FlowID,
               @SysAlias AS SysAlias,
               @dbg AS dbg,
               @DbgFileName AS dbgFileName,
               @cmdSchema AS cmdSchema,
               @Fullload AS Fullload,
               @srcIsSynapse AS srcIsSynapse,
               @trgIsSynapse AS trgIsSynapse,
               @truncateTrg AS truncateTrg,
               @IncrementalColumns AS IncrementalColumns,
               @NoOfThreads AS NoOfThreads,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @FlowType AS FlowType,
               
               @srcDSType AS srcDSType,
               @tmpSchema AS tmpSchema,
               @RemoveInColumnName AS RemoveInColumnName,
               @IncrementalClauseExp AS IncrementalClauseExp,
               @InitLoad AS InitLoad,
               @InitLoadFromDate AS InitLoadFromDate,
               @InitLoadToDate AS InitLoadToDate,
               @InitLoadBatchBy AS InitLoadBatchBy,
               @InitLoadBatchSize AS InitLoadBatchSize,
               @InitLoadKeyMaxValue AS InitLoadKeyMaxValue,
               @InitLoadKeyColumn AS InitLoadKeyColumn,
               @WhereIncExp AS WhereIncExp,
               @WhereDateExp AS WhereDateExp,
               @WhereXML AS WhereXML,
               @srcFilterIsAppend AS srcFilterIsAppend,
               @FetchMinValuesFromSysLog AS FetchMinValuesFromSysLog,
               @TrgIndexes AS TrgIndexes,

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
			   @trgDesiredIndex AS trgDesiredIndex
			   

    END;

    EXEC flw.GetRVIncrementalDS @FlowID = @FlowID,
                                @DateColumn = @DateColumn,
                                @IncrementalColumns = @IncrementalColumns,
                                @SourceType = @srcDSType;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to build a runtime dataset for ADO Dataflows', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVFlowADO', NULL, NULL
GO
