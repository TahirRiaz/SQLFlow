SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVPreFlowXML]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure builds a metadata dataset for ingesting an XML file.
  -- Summary			:	The purpose of this stored procedure is to retrieve configuration parameters and input parameters for a given data flow, and to execute a separate stored procedure 
							named "ExecPreFlowXML" with those parameters.

							The parameters retrieved by "GetRVPreFlowXML" include various timeout values, batch sizes, and retry settings, as well as file paths, database connections, 
							and other data source and target information. The stored procedure also constructs a dynamic SQL statement to execute "ExecPreFlowXML" with the retrieved parameters.

							The "ExecPreFlowXML" stored procedure performs several data transformation and validation steps on input XML data and loads it into a target database. 
							These steps include creating a schema for the target table if it does not exist, validating and parsing the XML data, transforming the data into a target format, 
							and bulk loading the data into the target table.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVPreFlowXML]
    @FlowID INT,                      -- FlowID from [flw].[Ingestion]
    @FlowType VARCHAR(50) = 'xml',
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
    DECLARE @DefaultColDataType NVARCHAR(255);


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
            @SearchSubDirectories BIT = 1,
            @srcPath NVARCHAR(255) = N'',
            @FileDate NVARCHAR(15) = N'',
            @hierarchyIdentifier NVARCHAR(255) = N'',
            @SysAlias [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @copyToPath NVARCHAR(255) = N'',
            @ShowPathWithFileName BIT = 0,
            @srcPathMask NVARCHAR(255),
            @preFilter NVARCHAR(1024),
            @trgIsSynapse BIT = 0,
            @srcDeleteAtPath BIT = 0,
            @ExpectedColumnCount INT = 0,
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
            @TrgIndexes NVARCHAR(MAX),
            @XmlToDataTableCode NVARCHAR(MAX),
			@trgDesiredIndex NVARCHAR(MAX);

    SELECT @Schema07Pre = [flw].[RemBrackets](ParamValue)
    FROM flw.SysCFG
    WHERE (ParamName = N'Schema07Pre');


    SELECT @srcFile = csv.srcFile,
           @trgConnectionString = ds.ConnectionString,
           @trgDBSchTbl = flw.GetValidSrcTrgName(csv.trgDBSchTbl),
           @srcDeleteIngested = ISNULL(csv.srcDeleteIngested, 0),
           @SyncSchema = ISNULL(SyncSchema, 0),
           @SearchSubDirectories = ISNULL(SearchSubDirectories, 0),
           @srcPath = ISNULL(csv.srcPath, ''),
           @FileDate = CASE
                           WHEN LEN(pLog.FileDate) > 0 THEN
                               pLog.FileDate
                           ELSE
                               '0'
                       END,
           @hierarchyIdentifier = ISNULL(hierarchyIdentifier, ''),
           @trgIsSynapse = ISNULL(ds.IsSynapse, 0),
           @SysAlias = ISNULL(csv.SysAlias, ''),
           @Batch = ISNULL(csv.Batch, ''),
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @FileDate = ISNULL(pLog.FileDate, 0),
           @copyToPath = ISNULL(copyToPath, ''),
           @ShowPathWithFileName = ISNULL(ShowPathWithFileName, 0),
           @srcPathMask = ISNULL(srcPathMask, ''),
           @preFilter = REPLACE(ISNULL(preFilter, ''), CHAR(39), ''''''),
           @NoOfThreads = CASE
                              WHEN csv.NoOfThreads > 0 THEN
                                  csv.NoOfThreads
                              ELSE
                                  @NoOfThreads
                          END,
           @DefaultColDataType = CASE
                                     WHEN LEN(DefaultColDataType) > 0 THEN
                                         DefaultColDataType
                                     ELSE
                                         [flw].[GetCFGParamVal]('DefaultColDataType')
                                 END,
           @ExpectedColumnCount = ISNULL(ExpectedColumnCount, 0),
           @srcDeleteAtPath = ISNULL(srcDeleteAtPath, 0),
           @srcTenantId = ISNULL(ss.TenantId, ''),
           @srcSubscriptionId = ISNULL(ss.SubscriptionId, ''),
           @srcApplicationId = ISNULL(ss.ApplicationId, ''),
           @srcClientSecret = ISNULL(ss.ClientSecret, ''),
           @srcKeyVaultName = ISNULL(ss.KeyVaultName, ''),
           @srcSecretName = '', -- Cant use SecretName for root access for the App Account
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

           @FetchDataTypes = ISNULL(csv.FetchDataTypes, 0),
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
           @XmlToDataTableCode = ISNULL(XmlToDataTableCode, ''),
		   @trgDesiredIndex = CASE
                             WHEN LEN(trgDesiredIndex) > 0 THEN
                                 trgDesiredIndex
                             ELSE
                                 ''
                         END
    FROM [flw].[PreIngestionXML] csv
        INNER JOIN flw.SysDataSource ds
            ON csv.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON csv.FlowID = pLog.[FlowID]
               AND csv.FlowType = pLog.[FlowType]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
            ON csv.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE csv.FlowID = @FlowID;


    IF @dbg > 2
    BEGIN
        SET @curSection = N'90 :: ' + @curObjName + N' :: @DefaultColDataType Value';
        SET @curCode = @DefaultColDataType;
        PRINT [flw].[GetLogSection](@curSection, @curCode);
    END;

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

    SELECT @ViewCMD = ViewCMD,
           @ViewSelect = ViewSelect,
           @ViewName = ViewName,
           @ViewNameFull = @ViewNameFull,
           @preFilter = @preFilter
    FROM [flw].[GetPreViewCmd](@FlowID);

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
                WHERE ISNULL([Success],0) = 0
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
               @DefaultColDataType AS DefaultColDataType,
               @trgDatabase AS trgDatabase,
               @ViewName AS ViewName,
               @ViewNameFull AS ViewNameFull,
               @ViewCMD AS ViewCMD,
               @ViewSelect AS ViewSelect,
               @trgSchema AS trgSchema,
               @trgObject AS trgObject,
               @SyncSchema AS SyncSchema,
               @SearchSubDirectories AS SearchSubDirectories,
               @FileDate AS FileDate,
               @hierarchyIdentifier AS hierarchyIdentifier,
               @trgIsSynapse AS trgIsSynapse,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @FlowType AS FlowType,
               @OnErrorResume AS onErrorResume,
               @copyToPath AS copyToPath,
               @ShowPathWithFileName AS ShowPathWithFileName,
               @srcPathMask AS srcPathMask,
               @preFilter AS preFilter,
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
               @XmlToDataTableCode AS XmlToDataTableCode,
			   @trgDesiredIndex AS trgDesiredIndex,
               @dbg AS dbg; 

    END;

    EXEC flw.GetRVIncrementalDS @FlowID = @FlowID;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure builds a metadata dataset for ingesting an XML file.
  -- Summary			:	The purpose of this stored procedure is to retrieve configuration parameters and input parameters for a given data flow, and to execute a separate stored procedure 
							named "ExecPreFlowXML" with those parameters.

							The parameters retrieved by "GetRVPreFlowXML" include various timeout values, batch sizes, and retry settings, as well as file paths, database connections, 
							and other data source and target information. The stored procedure also constructs a dynamic SQL statement to execute "ExecPreFlowXML" with the retrieved parameters.

							The "ExecPreFlowXML" stored procedure performs several data transformation and validation steps on input XML data and loads it into a target database. 
							These steps include creating a schema for the target table if it does not exist, validating and parsing the XML data, transforming the data into a target format, 
							and bulk loading the data into the target table.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVPreFlowXML', NULL, NULL
GO
