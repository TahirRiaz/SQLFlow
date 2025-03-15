SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- Stored Procedure

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVFlowING]
  -- Date				:   2020.11.06
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to build a meta data dataset for Ingestion (SQL2SQL) Dataflows
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.06		Initial
  ##################################################################################################################################################
*/


CREATE PROCEDURE [flw].[GetRVFlowING]
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

    --DECLARE @FlowID [INT] = 7


    --Please see the table [flw].[SysCFG] for an explanation for each of these paramters
    DECLARE @BulkLoadTimeoutInSek INT = [flw].[GetCFGParamVal]('BulkLoadTimeoutInSek');
    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');
    DECLARE @BulkLoadBatchSize INT = [flw].[GetCFGParamVal]('BulkLoadBatchSize');
    DECLARE @NoOfThreads INT = [flw].[GetCFGParamVal]('NoOfThreads');
    DECLARE @MaxRetry INT = [flw].[GetCFGParamVal]('MaxRetry');
    DECLARE @RetryDelayMS INT = [flw].[GetCFGParamVal]('RetryDelayMS');
    DECLARE @stgSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema01Raw');
    DECLARE @ColCleanupSQLRegExp NVARCHAR(255) = [flw].[GetCFGParamVal]('ColCleanupSQLRegExp');
    DECLARE @ReplaceInvalidCharsWith NVARCHAR(2) = [flw].[GetCFGParamVal]('ReplaceInvalidCharsWith');

    DECLARE @srcConnectionString [NVARCHAR](250),
            @srcDBSchTbl [NVARCHAR](250),
            @trgConnectionString [NVARCHAR](250),
            @trgDBSchTbl [NVARCHAR](250),
            @SyncSchema [BIT],
            @OnSyncCleanColumnName [BIT],
            @OnSyncConvertUnicodeDataType [BIT],
            @KeyColumns [NVARCHAR](2048),
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
            @Tokenize BIT = 0,
            @srcFilter NVARCHAR(1024),
            @trgVersioning BIT = 0,
            @SysAlias [NVARCHAR](250) = N'',
            @TokenVersioning BIT = 0,
            @DbgFileName NVARCHAR(255) = N'',
            @cmdSchema NVARCHAR(MAX) = N'',
            @Fullload BIT = 0,
            @truncateTrg BIT = 0,
            @truncatePreTableOnCompletion BIT = 0,
            @srcIsSynapse BIT = 0,
            @trgIsSynapse BIT = 0,
            @FlowType [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @IncrementalColumns NVARCHAR(1024) = N'',
            @WhereIncExp NVARCHAR(MAX) = N'',
            @WhereDateExp NVARCHAR(MAX) = N'',
            @IdentityColumn NVARCHAR(255) = N'',
            @DataSetColumn NVARCHAR(255) = N'',
            @SkipUpdateExsisting BIT = 0,
            @SkipInsertNew BIT = 0,
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
            @skeyTenantId [NVARCHAR](100) = N'',
            @skeySubscriptionId [NVARCHAR](100) = N'',
            @skeyApplicationId [NVARCHAR](100) = N'',
            @skeyClientSecret [NVARCHAR](100) = N'',
            @skeyKeyVaultName [NVARCHAR](250) = N'',
            @skeySecretName [NVARCHAR](250) = N'',
            @skeyResourceGroup [NVARCHAR](250) = N'',
            @skeyDataFactoryName [NVARCHAR](250) = N'',
            @skeyAutomationAccountName [NVARCHAR](250) = N'',
            @skeyStorageAccountName [NVARCHAR](250) = N'',
            @skeyBlobContainer [NVARCHAR](250) = N'',
            @IncrementalClauseExp NVARCHAR(1024) = N'',
            @ColumnStoreIndexOnTrg BIT = 0,
            @FetchMinValuesFromSrc BIT = 1,
            @srcFilterIsAppend BIT = 1,
            @CleanColumnNameSQLRegExp NVARCHAR(1024) = N'',
            @HashKeyColumns NVARCHAR(1024) = N'',
            @HashKeyType NVARCHAR(25) = N'',
            @DataType NVARCHAR(25) = N'',
            @DataTypeExp NVARCHAR(25) = N'',
            @InsertUnknownDimRow BIT = 1,
            @MatchKeysInSrcTrg BIT = 0,
            @InitLoad BIT = 1,
            @InitLoadFromDate NVARCHAR(12), --date = GETDATE(),
            @InitLoadToDate NVARCHAR(12),   --date = GETDATE(),
            @InitLoadBatchBy VARCHAR(1) = 'M',
            @InitLoadBatchSize INT,
            @InitLoadKeyMaxValue INT,
            @InitLoadKeyColumn NVARCHAR(255),
            @IgnoreColumnsInHashkey NVARCHAR(1024),
            @trgDesiredIndex NVARCHAR(MAX),
            @UseBatchUpsertToAvoideLockEscalation BIT = 1,
            @BatchUpsertRowCount int = 2000;



    --@Tokenize = ISNULL(Tokenize, 0),
    --Can cause confusion as a bit flag on Ingestion flow
    SET @Tokenize = IIF((SELECT COUNT(*)FROM [flw].[IngestionTokenize] src WHERE src.FlowID = @FlowID) > 0, 1, 0);

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
        INNER JOIN flw.Ingestion b
            ON l.FlowID = b.FlowID
    WHERE l.FlowID = @FlowID;

    SELECT TOP 1
           @srcConnectionString = srcBDS.ConnectionString,
           @srcDBSchTbl = [flw].[StrCleanup](flw.GetValidSrcTrgName(srcDBSchTbl)),
           @trgConnectionString = trgBDS.ConnectionString,
           @trgDBSchTbl = [flw].[StrCleanup](flw.GetValidSrcTrgName(trgDBSchTbl)),
           @SyncSchema = ISNULL(SyncSchema, 0),
           @ReplaceInvalidCharsWith = COALESCE(ReplaceInvalidCharsWith, @ReplaceInvalidCharsWith, ''),
           @OnSyncCleanColumnName = ISNULL(OnSyncCleanColumnName, 0),
           @OnSyncConvertUnicodeDataType = ISNULL(OnSyncConvertUnicodeDataType, 0),
           @KeyColumns = ISNULL([flw].[ListCleanup]([flw].[StrCleanup](KeyColumns)), ''),
           @DateColumn = ISNULL([flw].[StrCleanup](DateColumn), ''),
           @NoOfOverlapDays = ISNULL(NoOfOverlapDays, 0),
           @StreamData = ISNULL(StreamData, 0),
           @PreProcessOnTrg = ISNULL(LTRIM(RTRIM(PreProcessOnTrg)), ''),
           @PostProcessOnTrg = ISNULL(LTRIM(RTRIM(PostProcessOnTrg)), ''),
           @IgnoreColumns = ISNULL([flw].[ListCleanup]([flw].[StrCleanup](IgnoreColumns)), ''),
           @srcFilter = ISNULL([flw].[StrCleanup](srcFilter), ''),
           @trgVersioning = ISNULL(trgVersioning, 0),
           @SysAlias = ISNULL(SysAlias, ''),
           @TokenVersioning = ISNULL(TokenVersioning, 0),
           @Fullload = ISNULL(FullLoad, 0),
           @srcIsSynapse = ISNULL(srcBDS.IsSynapse, 0),
           @trgIsSynapse = ISNULL(trgBDS.IsSynapse, 0),
           @truncateTrg = ISNULL(TruncateTrg, 0),
           @truncatePreTableOnCompletion = ISNULL(b.TruncatePreTableOnCompletion, 0),
           @IncrementalColumns = ISNULL([flw].[ListCleanup]([flw].[StrCleanup](b.IncrementalColumns)), ''),
           @SysAlias = ISNULL(SysAlias, ''),
           @Batch = ISNULL(Batch, ''),
           @FlowType = ISNULL(FlowType, 'ing'),
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @NoOfThreads = CASE
                              WHEN ISNULL(b.NoOfThreads, 0) = 0 THEN
                                  @NoOfThreads
                              ELSE
                                  b.NoOfThreads
                          END,
           @Batch = ISNULL(Batch, ''),
           @IdentityColumn = ISNULL(IdentityColumn, ''),
           @DataSetColumn = ISNULL(DataSetColumn, ''),
           @SkipUpdateExsisting = ISNULL(SkipUpdateExsisting, 0),
           @SkipInsertNew = ISNULL(SkipInsertNew, 0),
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
           @ColumnStoreIndexOnTrg = ISNULL(b.ColumnStoreIndexOnTrg, 0),
           @IncrementalClauseExp = ISNULL(b.IncrementalClauseExp, ''),
           @ColCleanupSQLRegExp = COALESCE(CleanColumnNameSQLRegExp, @ColCleanupSQLRegExp, ''),
           @FetchMinValuesFromSrc = ISNULL(FetchMinValuesFromSrc, 1),
           @srcFilterIsAppend = ISNULL(srcFilterIsAppend, 1),
           @HashKeyColumns = ISNULL(HashKeyColumns, ''),
           @HashKeyType = ISNULL(hkType.HashKeyType, 'SHA2_256'),
           @DataType = ISNULL(hkType.DataType, 'BINARY'),
           @DataTypeExp = ISNULL(hkType.DataTypeExp, 'BINARY(32)'),
           @InsertUnknownDimRow = ISNULL(InsertUnknownDimRow, 0),
           @MatchKeysInSrcTrg = ISNULL(MatchKeysInSrcTrg, 0),
           @trgDesiredIndex = CASE
                                  WHEN LEN(trgDesiredIndex) > 0 THEN
                                      trgDesiredIndex
                                  ELSE
                                      ''
                              END,
           @InitLoad = ISNULL(InitLoad, 0),
           @InitLoadFromDate = CONVERT(VARCHAR, ISNULL(InitLoadFromDate, DATEADD(YEAR, -3, GETDATE())), 23),
           @InitLoadToDate = CONVERT(VARCHAR, ISNULL(InitLoadToDate, GETDATE()), 23),
           @InitLoadBatchBy = ISNULL(InitLoadBatchBy, 'M'),
           @InitLoadBatchSize = ISNULL(InitLoadBatchSize, 1),
           @InitLoadKeyMaxValue = ISNULL(InitLoadKeyMaxValue, 10000000),
           @InitLoadKeyColumn = ISNULL(InitLoadKeyColumn, ''),
           @IgnoreColumnsInHashkey = ISNULL(IgnoreColumnsInHashkey, ''),
           @UseBatchUpsertToAvoideLockEscalation = ISNULL(UseBatchUpsertToAvoideLockEscalation, 0),
           @BatchUpsertRowCount = ISNULL(BatchUpsertRowCount, 2000)
    FROM flw.Ingestion b
        INNER JOIN flw.SysDataSource srcBDS
            ON srcBDS.Alias = b.srcServer
               OR srcBDS.DatabaseName = b.srcServer
               OR CAST(srcBDS.DataSourceID AS VARCHAR(255)) = b.srcServer
        INNER JOIN flw.SysDataSource trgBDS
            ON trgBDS.Alias = b.trgServer
               OR trgBDS.DatabaseName = b.trgServer
               OR CAST(trgBDS.DataSourceID AS VARCHAR(255)) = b.trgServer
        LEFT OUTER JOIN [flw].[SysHashKeyType] hkType
            ON hkType.[HashKeyType] = b.[HashKeyType]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON srcBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON trgBDS.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE b.FlowID = @FlowID;


    SELECT @srcDatabase = [1],
           @srcSchema = [2],
           @srcObject = [3]
    FROM
    (
        SELECT Ordinal,
               Item
        FROM flw.StringSplit(flw.GetValidSrcTrgName(@srcDBSchTbl), '.')
    ) AS SourceTable
    PIVOT
    (
        MAX(Item)
        FOR Ordinal IN ([1], [2], [3])
    ) AS PivotTable;

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

        IF @dbg >= 2
        BEGIN
            SET @curSection = N'110 :: ' + @curObjName + N' :: @cmdSchema - Dynamic SQL Statement';
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

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
               @KeyColumns AS KeyColumns,
               @DateColumn AS DateColumn,
               @SyncSchema AS SyncSchema,
               @ReplaceInvalidCharsWith AS ReplaceInvalidCharsWith,
               @OnSyncCleanColumnName AS OnSyncCleanColumnName,
               @OnSyncConvertUnicodeDataType AS OnSyncConvertUnicodeDataType,
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
               @Tokenize AS Tokenize,
               @srcFilter AS srcFilter,
               @FlowID AS FlowID,
               @trgVersioning AS trgVersioning,
               @SysAlias AS SysAlias,
               @TokenVersioning AS TokenVersioning,
               @dbg AS dbg,
               @DbgFileName AS dbgFileName,
               @cmdSchema AS cmdSchema,
               @Fullload AS Fullload,
               @srcIsSynapse AS srcIsSynapse,
               @trgIsSynapse AS trgIsSynapse,
               @truncateTrg AS truncateTrg,
               @truncatePreTableOnCompletion AS truncatePreTableOnCompletion,
               @IncrementalColumns AS IncrementalColumns,
               @NoOfThreads AS NoOfThreads,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @FlowType AS FlowType,
               @WhereIncExp AS WhereIncExp,
               @WhereDateExp AS WhereDateExp,
               @IdentityColumn AS IdentityColumn,
               @DataSetColumn AS DataSetColumn,
               @SkipUpdateExsisting AS SkipUpdateExsisting,
               @SkipInsertNew AS SkipInsertNew,
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
               @ColumnStoreIndexOnTrg AS ColumnStoreIndexOnTrg,
               @IncrementalClauseExp AS IncrementalClauseExp,
               @FetchMinValuesFromSrc AS FetchMinValuesFromSrc,
               @srcFilterIsAppend AS srcFilterIsAppend,
               @HashKeyColumns AS HashKeyColumns,
               @HashKeyType AS HashKeyType,
               @DataType AS DataType,
               @DataTypeExp AS DataTypeExp,
               @InsertUnknownDimRow AS InsertUnknownDimRow,
               @trgDesiredIndex AS trgDesiredIndex,
               @MatchKeysInSrcTrg AS MatchKeysInSrcTrg,
               @InitLoad AS InitLoad,
               @InitLoadFromDate AS InitLoadFromDate,
               @InitLoadToDate AS InitLoadToDate,
               @InitLoadBatchBy AS InitLoadBatchBy,
               @InitLoadBatchSize AS InitLoadBatchSize,
               @InitLoadKeyMaxValue AS InitLoadKeyMaxValue,
               @InitLoadKeyColumn AS InitLoadKeyColumn,
               @IgnoreColumnsInHashkey AS IgnoreColumnsInHashkey,
               @UseBatchUpsertToAvoideLockEscalation AS UseBatchUpsertToAvoideLockEscalation,
               @BatchUpsertRowCount AS BatchUpsertRowCount;

        SELECT i.FlowID,
               i.trgDBSchTbl AS trgDBSchTbl,
               PARSENAME(i.trgDBSchTbl, 3) AS [baseDatabase],
               PARSENAME(i.trgDBSchTbl, 2) AS [baseSchema],
               'tmp' AS [baseTmpSchema],
               PARSENAME(i.trgDBSchTbl, 1) AS [baseObject],
               skey.SurrogateDbSchTbl,
               PARSENAME(skey.SurrogateDbSchTbl, 3) AS [SKeyDatabase],
               PARSENAME(skey.SurrogateDbSchTbl, 2) AS [SKeySchema],
               'tmp' AS [SKeyTmpSchema],
               PARSENAME(skey.SurrogateDbSchTbl, 1) AS [SKeyObject],
               skey.SurrogateColumn,
               skey.KeyColumns,
               skey.sKeyColumns,
               ISNULL(trgBDS2.ConnectionString, trgBDS.ConnectionString) AS SKeyConString,
               ISNULL(skv2.TenantId, skv1.TenantId) AS skeyTenantId,
               ISNULL(skv2.SubscriptionId, skv1.SubscriptionId) AS skeySubscriptionId,
               ISNULL(skv2.ApplicationId, skv1.ApplicationId) AS skeyApplicationId,
               ISNULL(skv2.ClientSecret, skv1.ClientSecret) AS skeyClientSecret,
               ISNULL(skv2.KeyVaultName, skv1.KeyVaultName) AS skeyKeyVaultName,
               ISNULL(trgBDS2.KeyVaultSecretName, trgBDS.KeyVaultSecretName) AS skeySecretName,
               ISNULL(skv2.ResourceGroup, skv1.ResourceGroup) AS skeyResourceGroup,
               ISNULL(skv2.DataFactoryName, skv1.DataFactoryName) AS skeyDataFactoryName,
               ISNULL(skv2.AutomationAccountName, skv1.AutomationAccountName) AS skeyAutomationAccountName,
               ISNULL(skv2.StorageAccountName, skv1.StorageAccountName) AS skeyStorageAccountName,
               ISNULL(skv2.BlobContainer, skv1.BlobContainer) AS skeyBlobContainer,
               ISNULL(trgBDS2.IsSynapse, trgBDS.IsSynapse) AS IsSynapse,
               CAST(CASE
                        WHEN LEN(ISNULL([SurrogateServer], '')) > 1
                             AND (ISNULL(trgBDS.Alias, '') != ISNULL(trgBDS2.Alias, '')) THEN
                            1
                        ELSE
                            NULL
                    END AS BIT) AS SKeyIsRemote,
               skey.[PreProcess],
               skey.[PostProcess]
        FROM flw.SurrogateKey skey
            LEFT OUTER JOIN flw.Ingestion AS i
                ON i.FlowID = skey.FlowID
            INNER JOIN flw.SysDataSource AS trgBDS
                ON trgBDS.Alias = i.trgServer
            LEFT OUTER JOIN flw.SysDataSource AS trgBDS2
                ON trgBDS2.Alias = skey.SurrogateServer
            LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
                ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
            LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
                ON trgBDS2.ServicePrincipalAlias = skv2.ServicePrincipalAlias
        WHERE i.FlowID = @FlowID;

        EXEC [flw].[GetRVPrevDS] @FlowID;


        EXEC [flw].[GetTransformSchemaDS] @FlowID;

        SELECT [GeoCodingID],
               [FlowID],
               [GoogleAPIKey],
               [KeyColumn],
               [LonColumn],
               [LatColumn],
               [AddressColumn],
               [trgDBSchTbl]
        FROM [flw].[GeoCoding]
        WHERE [FlowID] = @FlowID;



    END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to build a meta data dataset for Ingestion (SQL2SQL) Dataflows', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVFlowING', NULL, NULL
GO
