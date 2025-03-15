SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



--[flw].[ExecPreFlowPRQ] 1
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVPreFlowPRQ]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure builds a metadata dataset for ingesting an Excel file.
  -- Summary			:	Input parameters:

							@FlowID INT
							@FlowType VARCHAR(50) with default value 'PRQ'
							@ExecMode VARCHAR(50) with default value 'Manual'
							@AutoCreateSchema INT with default value 1
							@dbg INT with default value 0
							The main purpose of this stored procedure is to retrieve various configuration values related to a specific flow and perform some data
							manipulations before executing the flow. It seems to be designed for working with Excel files (as the name suggests). 
							The stored procedure retrieves different values from the [flw] schema tables and performs several calculations and string manipulations.

							For example, it fetches values for BulkLoadTimeoutInSek, GeneralTimeoutInSek, BulkLoadBatchSize, NoOfThreads, MaxRetry, RetryDelayMS, 
							and other configuration parameters. It also constructs a dynamic SQL statement to execute the [flw].[ExecPreFlowPRQ] function with the appropriate parameters.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVPreFlowPRQ]
    @FlowID INT,                      -- FlowID from [flw].[Ingestion]
    @FlowType VARCHAR(50) = 'prq',
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
            @SearchSubDirectories BIT = 1,
            @srcPath NVARCHAR(255) = N'',
            @FileDate NVARCHAR(15) = N'',
            @SysAlias [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @copyToPath NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @ShowPathWithFileName BIT = 0,
            @srcPathMask NVARCHAR(255),
            @preFilter NVARCHAR(1024),
            @trgIsSynapse BIT = 0,
            @ExpectedColumnCount INT = 0,
            @srcDeleteAtPath BIT = 0,
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
            @InitFromFileDate VARCHAR(15) = '',
            @InitToFileDate VARCHAR(15) = '',
            @DefaultColDataType NVARCHAR(255) = N'',
            @TrgIndexes NVARCHAR(MAX),
            @trgDesiredIndex NVARCHAR(MAX);

    SELECT @Schema07Pre = [flw].[RemBrackets](ParamValue)
    FROM flw.SysCFG
    WHERE (ParamName = N'Schema07Pre');


    SELECT @srcFile = prq.srcFile,
           @trgConnectionString = ds.ConnectionString,
           @trgDBSchTbl = flw.GetValidSrcTrgName(prq.trgDBSchTbl),
           @srcDeleteIngested = ISNULL(prq.srcDeleteIngested, 0),
           @SyncSchema = ISNULL(SyncSchema, 0),
           @PartitionList = ISNULL(PartitionList, ''),
           @SearchSubDirectories = ISNULL(SearchSubDirectories, 0),
           @srcPath = ISNULL(prq.srcPath, ''),
           @FileDate = CASE
                           WHEN LEN(pLog.FileDate) > 0 THEN
                               pLog.FileDate
                           ELSE
                               '0'
                       END,
           @trgIsSynapse = ISNULL(ds.IsSynapse, 0),
           @SysAlias = ISNULL(prq.SysAlias, ''),
           @Batch = ISNULL(prq.Batch, ''),
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @FlowType = ISNULL(prq.FlowType, ''),
           @copyToPath = ISNULL(copyToPath, ''),
           @ShowPathWithFileName = ISNULL(ShowPathWithFileName, ''),
           @preFilter = REPLACE(ISNULL(preFilter, ''), CHAR(39), ''''''),
           @srcPathMask = ISNULL(srcPathMask, ''),
           @NoOfThreads = CASE
                              WHEN prq.NoOfThreads > 0 THEN
                                  prq.NoOfThreads
                              ELSE
                                  @NoOfThreads
                          END,
           @ExpectedColumnCount = ISNULL(ExpectedColumnCount, 0),
           @srcDeleteAtPath = ISNULL(srcDeleteAtPath, 0),
           @srcTenantId = ISNULL(ss.TenantId, ''),
           @srcSubscriptionId = ISNULL(ss.SubscriptionId, ''),
           @srcApplicationId = ISNULL(ss.ApplicationId, ''),
           @srcClientSecret = ISNULL(ss.ClientSecret, ''),
           @srcKeyVaultName = ISNULL(ss.KeyVaultName, ''),
           @srcSecretName = N'', -- Cant use SecretName for root access for the App Account
           @srcResourceGroup = ISNULL(ss.ResourceGroup, ''),
           @srcDataFactoryName = ISNULL(ss.DataFactoryName, ''),
           @srcAutomationAccountName = ISNULL(ss.AutomationAccountName, ''),
           @srcStorageAccountName = ISNULL(ss.StorageAccountName, ''),
           @srcBlobContainer = ISNULL(ss.BlobContainer, ''),
           @trgTenantId = ISNULL(skv1.TenantId, ''),
           @trgSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           @trgApplicationId = ISNULL(skv1.ApplicationId, ''),
           @trgClientSecret = ISNULL(skv1.ClientSecret, ''),
           @trgKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
           @trgSecretName = ISNULL(ds.KeyVaultSecretName, ''),
           @trgResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           @trgDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           @trgAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           @trgStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           @trgBlobContainer = ISNULL(skv1.BlobContainer, ''),
           @FetchDataTypes = ISNULL(prq.FetchDataTypes, 0),
           @InitFromFileDate = CASE
                                   WHEN LEN(InitFromFileDate) > 0 THEN
                                       InitFromFileDate
                                   ELSE
                                       '0'
                               END,
           @InitToFileDate = CASE
                                 WHEN LEN(InitToFileDate) > 0 THEN
                                     InitToFileDate
                                 ELSE
                                     '0'
                             END,
           @TrgIndexes = CASE
                             WHEN LEN(TrgIndexes) > 0 THEN
                                 TrgIndexes
                             ELSE
                                 ''
                         END,
           @DefaultColDataType = CASE
                                     WHEN LEN(DefaultColDataType) > 0 THEN
                                         DefaultColDataType
                                     ELSE
                                         [flw].[GetCFGParamVal]('DefaultColDataType')
                                 END,
           @trgDesiredIndex = CASE
                                  WHEN LEN(trgDesiredIndex) > 0 THEN
                                      trgDesiredIndex
                                  ELSE
                                      ''
                              END
    FROM [flw].[PreIngestionPRQ] prq
        INNER JOIN flw.SysDataSource ds
            ON prq.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON prq.FlowID = pLog.[FlowID]
               AND prq.FlowType = pLog.[FlowType]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
            ON prq.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE prq.FlowID = @FlowID;




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
        SET @curCode
            = N'PRINT [flw].[GetFlowSchemaScript](''' + CAST(@FlowID AS VARCHAR(255)) + N',''' + @trgDatabase
              + N''',''' + @trgSchema + N''',' + CAST(@Init AS VARCHAR(2)) + N',' + CAST(@FirstExec AS VARCHAR(2))
              + N')';

        IF @dbg >= 2
        BEGIN
            SET @curSection = N'110 :: ' + @curObjName + N' :: @cmdSchema - Dynamic SQL Statement';
            PRINT [flw].[GetLogSection](@curSection, @curCode);
        END;

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
               @SearchSubDirectories AS SearchSubDirectories,
               @FileDate AS FileDate,
               @trgIsSynapse AS trgIsSynapse,
               @dbg AS dbg,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @copyToPath AS copyToPath,
               @ShowPathWithFileName AS ShowPathWithFileName,
               @srcPathMask AS srcPathMask,
               @preFilter AS preFilter,
               @FlowType AS FlowType,
               @ExpectedColumnCount AS ExpectedColumnCount,
               @srcDeleteAtPath AS srcDeleteAtPath,
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
               @FetchDataTypes AS FetchDataTypes,
               @PreIngTransStatus AS PreIngTransStatus,
               @InitFromFileDate AS InitFromFileDate,
               @InitToFileDate AS InitToFileDate,
               @TrgIndexes AS TrgIndexes,
               @DefaultColDataType AS DefaultColDataType,
			   @trgDesiredIndex AS trgDesiredIndex
    END;

    EXEC flw.GetRVIncrementalDS @FlowID = @FlowID;


END;
GO
