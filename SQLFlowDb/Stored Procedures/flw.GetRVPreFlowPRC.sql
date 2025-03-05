SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




--[flw].[ExecPreFlowPRC] 1
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVPreFlowPRC]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure builds a metadata dataset for ingesting an Excel file.
  -- Summary			:	Input parameters:

							@FlowID INT
							@FlowType VARCHAR(50) with default value 'PRC'
							@ExecMode VARCHAR(50) with default value 'Manual'
							@AutoCreateSchema INT with default value 1
							@dbg INT with default value 0
							The main purpose of this stored procedure is to retrieve various configuration values related to a specific flow and perform some data
							manipulations before executing the flow. It seems to be designed for working with Excel files (as the name suggests). 
							The stored procedure retrieves different values from the [flw] schema tables and performs several calculations and string manipulations.

							For example, it fetches values for BulkLoadTimeoutInSek, GeneralTimeoutInSek, BulkLoadBatchSize, NoOfThreads, MaxRetry, RetryDelayMS, 
							and other configuration parameters. It also constructs a dynamic SQL statement to execute the [flw].[ExecPreFlowPRC] function with the appropriate parameters.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVPreFlowPRC]
    @FlowID INT,                      -- FlowID from [flw].[Ingestion]
    @FlowType VARCHAR(50) = 'prc',
    @ExecMode VARCHAR(50) = 'Manual', -- Manual, Batch, Initial, Schedule
    @AutoCreateSchema INT = 1,
    @dbg INT = 0                      -- Show details. returns true/false if not
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


    --Please see the table [flw].[BulkloadCFG] for an explanation for each of these paramters
    DECLARE @BulkLoadTimeoutInSek INT = [flw].[GetCFGParamVal]('BulkLoadTimeoutInSek');
    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');
    DECLARE @BulkLoadBatchSize INT = [flw].[GetCFGParamVal]('BulkLoadBatchSize');
    DECLARE @NoOfThreads INT = [flw].[GetCFGParamVal]('NoOfThreads');
    DECLARE @MaxRetry INT = [flw].[GetCFGParamVal]('MaxRetry');
    DECLARE @RetryDelayMS INT = [flw].[GetCFGParamVal]('RetryDelayMS');
    DECLARE @stgSchema [NVARCHAR](255) = [flw].[GetCFGParamVal]('Schema01Raw');
    DECLARE @ColCleanupSQLRegExp NVARCHAR(255) = [flw].[GetCFGParamVal]('ColCleanupSQLRegExp');

    DECLARE @srcFile [NVARCHAR](250),
            @srcConString [NVARCHAR](250),
            @trgConnectionString [NVARCHAR](250),
            @trgDBSchTbl [NVARCHAR](250),
            @srcDeleteIngested BIT = 0,
            @trgDatabase [NVARCHAR](255) = N'',
            @trgSchema [NVARCHAR](250) = N'',
            @trgObject [NVARCHAR](250) = N'',
            @PreProcessOnTrg [NVARCHAR](250) = N'',
            @PostProcessOnTrg [NVARCHAR](250) = N'',
            @DbgFileName NVARCHAR(255) = N'',
            @cmdSchema NVARCHAR(MAX) = N'',
            @Schema07Pre NVARCHAR(25) = N'',
            @ViewName NVARCHAR(255) = N'',
            @ViewNameFull NVARCHAR(255) = N'',
            @ViewCMD NVARCHAR(MAX) = N'',
            @ViewColumnList NVARCHAR(MAX) = N'',
            @ViewSelect NVARCHAR(MAX) = N'',
            @SyncSchema BIT = 1,
            @PartitionList [NVARCHAR](250) = N'',
            @srcPath NVARCHAR(255) = N'',
            @FileDate NVARCHAR(15) = N'',
            @SysAlias [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @ShowPathWithFileName BIT = 0,
            @srcPathMask NVARCHAR(255),
            @preFilter NVARCHAR(1024),
            @trgIsSynapse BIT = 0,
            @ExpectedColumnCount INT = 0,
            @dlTenantId [NVARCHAR](100) = N'',
            @dlSubscriptionId [NVARCHAR](100) = N'',
            @dlApplicationId [NVARCHAR](100) = N'',
            @dlClientSecret [NVARCHAR](100) = N'',
            @dlKeyVaultName [NVARCHAR](250) = N'',
            @dlSecretName [NVARCHAR](250) = N'',
            @dlResourceGroup [NVARCHAR](250) = N'',
            @dlDataFactoryName [NVARCHAR](250) = N'',
            @dlAutomationAccountName [NVARCHAR](250) = N'',
            @dlStorageAccountName [NVARCHAR](250) = N'',
            @dlBlobContainer [NVARCHAR](250) = N'',
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
            @PreIngTransStatus BIT = [flw].[GetPreIngTransStatus](@FlowID),
            @FetchDataTypes BIT = 0,
            @TrgIndexes NVARCHAR(MAX),
            @srcCode NVARCHAR(MAX),
            @trgDesiredIndex NVARCHAR(MAX);

    SELECT @Schema07Pre = [flw].[RemBrackets](ParamValue)
    FROM flw.SysCFG
    WHERE (ParamName = N'Schema07Pre');


    SELECT @srcFile = PRC.srcFile,
           @trgConnectionString = ds.ConnectionString,
           @trgDBSchTbl = flw.GetValidSrcTrgName(PRC.trgDBSchTbl),
           @SyncSchema = ISNULL(SyncSchema, 0),
           @srcPath = ISNULL(PRC.srcPath, ''),
           @FileDate = CASE
                           WHEN LEN(pLog.FileDate) > 0 THEN
                               pLog.FileDate
                           ELSE
                               '0'
                       END,
           @trgIsSynapse = ISNULL(ds.IsSynapse, 0),
           @SysAlias = ISNULL(PRC.SysAlias, ''),
           @Batch = ISNULL(PRC.Batch, ''),
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @FlowType = ISNULL(PRC.FlowType, ''),
           @ShowPathWithFileName = ISNULL(ShowPathWithFileName, ''),
           @preFilter = REPLACE(ISNULL(preFilter, ''), CHAR(39), ''''''),
           @NoOfThreads = CASE
                              WHEN PRC.NoOfThreads > 0 THEN
                                  PRC.NoOfThreads
                              ELSE
                                  @NoOfThreads
                          END,
           @ExpectedColumnCount = ISNULL(ExpectedColumnCount, 0),
           @dlTenantId = ISNULL(ss.TenantId, ''),
           @dlSubscriptionId = ISNULL(ss.SubscriptionId, ''),
           @dlApplicationId = ISNULL(ss.ApplicationId, ''),
           @dlClientSecret = ISNULL(ss.ClientSecret, ''),
           @dlKeyVaultName = ISNULL(ss.KeyVaultName, ''),
           @dlSecretName = N'', -- Cant use SecretName for root access for the App Account
           @dlResourceGroup = ISNULL(ss.ResourceGroup, ''),
           @dlDataFactoryName = ISNULL(ss.DataFactoryName, ''),
           @dlAutomationAccountName = ISNULL(ss.AutomationAccountName, ''),
           @dlStorageAccountName = ISNULL(ss.StorageAccountName, ''),
           @dlBlobContainer = ISNULL(ss.BlobContainer, ''),
           @srcTenantId = ISNULL(skv2.TenantId, ''),
           @srcSubscriptionId = ISNULL(skv2.SubscriptionId, ''),
           @srcApplicationId = ISNULL(skv2.ApplicationId, ''),
           @srcClientSecret = ISNULL(skv2.ClientSecret, ''),
           @srcKeyVaultName = ISNULL(skv2.[KeyVaultName], ''),
           @srcSecretName = ISNULL(ds2.KeyVaultSecretName, ''),
           @srcResourceGroup = ISNULL(skv2.ResourceGroup, ''),
           @srcDataFactoryName = ISNULL(skv2.DataFactoryName, ''),
           @srcAutomationAccountName = ISNULL(skv2.AutomationAccountName, ''),
           @srcStorageAccountName = ISNULL(skv2.StorageAccountName, ''),
           @srcBlobContainer = ISNULL(skv2.BlobContainer, ''),
           @trgTenantId = ISNULL(skv1.TenantId, ''),
           @trgSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           @trgApplicationId = ISNULL(skv1.ApplicationId, ''),
           @trgClientSecret = ISNULL(skv1.ClientSecret, ''),
           @trgKeyVaultName = ISNULL(skv1.[KeyVaultName], ''),
           @trgSecretName = ISNULL(ds.KeyVaultSecretName, ''),
           @trgResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           @trgDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           @trgAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           @trgStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           @trgBlobContainer = ISNULL(skv1.BlobContainer, ''),
           @FetchDataTypes = ISNULL(PRC.FetchDataTypes, 0),
           @TrgIndexes = CASE
                             WHEN LEN(TrgIndexes) > 0 THEN
                                 TrgIndexes
                             ELSE
                                 ''
                         END,
           @srcCode = ISNULL(PRC.srcCode, ''),
           @srcConString = ds2.ConnectionString,
           @trgDesiredIndex = CASE
                                  WHEN LEN(trgDesiredIndex) > 0 THEN
                                      trgDesiredIndex
                                  ELSE
                                      ''
                              END
    FROM [flw].[PreIngestionPRC] PRC
        INNER JOIN flw.SysDataSource ds
            ON PRC.trgServer = ds.Alias
        INNER JOIN flw.SysDataSource ds2
            ON PRC.srcServer = ds2.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON PRC.FlowID = pLog.[FlowID]
               AND PRC.FlowType = pLog.[FlowType]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
            ON PRC.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON ds2.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE PRC.FlowID = @FlowID;

    SELECT @trgDatabase = [flw].[RemBrackets]([1]),
           @trgSchema = [flw].[RemBrackets]([2]),
           @trgObject = [flw].[RemBrackets]([3])
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

    SET @ViewName = N'[' + @Schema07Pre + N'].[v_' + @trgObject + N']';
    SET @ViewNameFull = N'[' + @trgDatabase + N'].[' + @Schema07Pre + N'].[v_' + @trgObject + N']';


    ;
    WITH colTbl
    AS (SELECT CASE
                   WHEN LEN(ISNULL(SelectExp, '')) > 0 THEN
                       REPLACE(REPLACE(SelectExp, CHAR(39), CHAR(39) + CHAR(39)), '@ColName', ColName)
                   ELSE
                       '[' + [flw].[RemBrackets](ColName) + ']'
               END + ' AS [' + CASE
                                   WHEN LEN(ISNULL(ColAlias, '')) > 0 THEN
                                       [flw].[RemBrackets](LTRIM(RTRIM(ColAlias)))
                                   ELSE
                                       [flw].[RemBrackets](ColName)
                               END + ']' AS ColName,
               [SortOrder],
               [TransfromID]
        FROM flw.PreIngestionTransfrom
        WHERE [FlowID] = @FlowID
              AND ISNULL(ExcludeColFromView, 0) = 0)
    SELECT @ViewCMD = ViewCMD,
           @ViewSelect = ViewSelect,
           @ViewName = ViewName,
           @ViewNameFull = @ViewNameFull,
           @preFilter = @preFilter
    FROM [flw].[GetPreViewCmd](@FlowID);


    DECLARE @rValue NVARCHAR(MAX);
    DECLARE @cmd VARCHAR(8000);
    DECLARE @c VARCHAR(25) = CHAR(39);

    SET @cmd
        = 'print [flw].[ExecPreFlowPRC] (' + @c + @trgConnectionString + @c + @stgSchema + @c + ',' + @c + @trgDatabase
          + @c + ',' + @c + @trgSchema + @c + ',' + @c + @trgObject + @c + CAST(@BulkLoadTimeoutInSek AS VARCHAR(25))
          + ',' + +CAST(@GeneralTimeoutInSek AS VARCHAR(25)) + ',' + CAST(@BulkLoadBatchSize AS VARCHAR(25)) + ','
          + +CAST(@NoOfThreads AS VARCHAR(25)) + ',' + CAST(@MaxRetry AS VARCHAR(25)) + ','
          + +CAST(@RetryDelayMS AS VARCHAR(25)) + ',' + CAST(@PreProcessOnTrg AS VARCHAR(25)) + ','
          + +CAST(@PostProcessOnTrg AS VARCHAR(25)) + @ColCleanupSQLRegExp + ',' + CAST(@dbg AS VARCHAR(25)) + ')';

    IF @dbg >= 2
    BEGIN
        SET @curSection = N'100 :: ' + @curObjName + N' :: @cmd - Dynamic SQL Statement';
        SET @curCode = @cmd;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

    --Set debug filename
    SET @DbgFileName
        = N'FLW_' + @FlowType + N'_' + [flw].[StrRemRegex](flw.GetValidSrcTrgName(@trgDBSchTbl), '%[^a-z]%') + N'_'
          + CAST(@FlowID AS VARCHAR(255)) + N'.txt';

    IF (@AutoCreateSchema = 1)
    BEGIN

        DECLARE @Init BIT = 1,
                @FirstExec BIT = 0;

        SELECT @Init = [ParamValue]
        FROM flw.SysCFG
        WHERE [ParamName] = 'CreateFlowSchema';

        IF (ISNULL(
            (
                SELECT COUNT([FlowID])
                FROM [flw].[SysLog]
                WHERE ISNULL([Success], 0) = 0
                      AND [FlowID] = @FlowID
            ),
            0
                  ) = 1
           )
        BEGIN
            SET @FirstExec = 1;
        END;

        SELECT @cmdSchema = [flw].[GetFlowSchemaScript](@FlowID, @trgDatabase, @trgSchema, @Init, @FirstExec);

    --print @SchemaRes
    END;

    IF (@trgConnectionString IS NOT NULL)
    BEGIN

        SELECT @srcPath AS srcPath,
               @srcFile AS srcFile,
               @srcDeleteIngested AS srcDeleteIngested,
               @trgConnectionString AS TrgConString,
               @trgDBSchTbl AS trgDBSchTbl,
               @BulkLoadTimeoutInSek AS BulkLoadTimeoutInSek,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @BulkLoadBatchSize AS BulkLoadBatchSize,
               @NoOfThreads AS NoOfThreads,
               @MaxRetry AS MaxRetry,
               @RetryDelayMS AS RetryDelayMS,
               @PreProcessOnTrg AS PreProcessOnTrg,
               @PostProcessOnTrg AS PostProcessOnTrg,
               @ColCleanupSQLRegExp AS ColCleanupSQLRegExp,
               @DbgFileName AS dbgFileName,
               @cmdSchema AS cmdSchema,
               @trgDatabase AS trgDatabase,
               @ViewName AS ViewName,
               @ViewNameFull AS ViewNameFull,
               @ViewCMD AS ViewCMD,
               @ViewSelect AS ViewSelect,
               @trgSchema AS trgSchema,
               @trgObject AS trgObject,
               @SyncSchema AS SyncSchema,
               @PartitionList AS PartitionList,
               @FileDate AS FileDate,
               @trgIsSynapse AS trgIsSynapse,
               @dbg AS dbg,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @ShowPathWithFileName AS ShowPathWithFileName,
               @srcPathMask AS srcPathMask,
               @preFilter AS preFilter,
               @FlowType AS FlowType,
               @ExpectedColumnCount AS ExpectedColumnCount,
               @dlTenantId AS dlTenantId,
               @dlSubscriptionId AS dlSubscriptionId,
               @dlApplicationId AS dlApplicationId,
               @dlClientSecret AS dlClientSecret,
               @dlKeyVaultName AS dlKeyVaultName,
               @dlSecretName AS dlSecretName,
               @dlResourceGroup AS dlResourceGroup,
               @dlDataFactoryName AS dlDataFactoryName,
               @dlAutomationAccountName AS dlAutomationAccountName,
               @dlStorageAccountName AS dlStorageAccountName,
               @dlBlobContainer AS dlBlobContainer,
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
               @trgKeyVaultName AS trgKeyVaultName,
               @trgSecretName AS trgSecretName,
               @FetchDataTypes AS FetchDataTypes,
               @PreIngTransStatus AS PreIngTransStatus,
               @srcCode AS srcCode,
               @TrgIndexes AS TrgIndexes,
               @srcConString AS srcConString,
               @srcKeyVaultName AS srcKeyVaultName,
               @srcSecretName AS srcSecretName,
               @trgDesiredIndex AS trgDesiredIndex;


    END;

    EXEC flw.GetRVIncrementalDS @FlowID = @FlowID;

    --Fetch SP Parameters
    ;
    SELECT P.ParameterID,
           P.FlowID,
           P.ParamAltServer,
           P.ParamName,
           P.SelectExp,
           P.PreFetch,
           P.Defaultvalue,
           ISNULL(dsAlt.SourceType, ds.SourceType) AS [SourceType],
           ISNULL(dsAlt.ConnectionString, ds.ConnectionString) AS trgConnectionString,
           trgTenantId = ISNULL(skv2.TenantId, skv1.TenantId),
           trgSubscriptionId = ISNULL(skv2.SubscriptionId, skv1.SubscriptionId),
           trgApplicationId = ISNULL(skv2.ApplicationId, skv1.ApplicationId),
           trgClientSecret = ISNULL(skv2.ClientSecret, skv1.ClientSecret),
           trgKeyVaultName = ISNULL(skv2.KeyVaultName, skv1.KeyVaultName),
           trgSecretName = ISNULL(dsAlt.KeyVaultSecretName, ds.KeyVaultSecretName),
           trgResourceGroup = ISNULL(skv2.ResourceGroup, skv1.ResourceGroup),
           trgDataFactoryName = ISNULL(skv2.DataFactoryName, skv1.DataFactoryName),
           trgAutomationAccountName = ISNULL(skv2.AutomationAccountName, skv1.AutomationAccountName),
           trgStorageAccountName = ISNULL(skv2.StorageAccountName, skv1.StorageAccountName),
           trgBlobContainer = ISNULL(skv2.BlobContainer, skv1.BlobContainer),
           ISNULL(dsAlt.IsSynapse, ds.IsSynapse) AS IsSynapse
    FROM flw.[Parameter] AS P
        INNER JOIN [flw].[PreIngestionPRC] prq
            ON P.FlowID = prq.FlowID
        LEFT OUTER JOIN flw.SysDataSource AS ds
            ON prq.trgServer = ds.Alias
        LEFT OUTER JOIN flw.SysDataSource AS dsAlt
            ON P.ParamAltServer = dsAlt.Alias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON dsAlt.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE P.FlowID = @FlowID;





END;
GO
