SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

--[flw].[ExecPreFlowEXP] 1
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVExport]
  -- Date				:   2023-03-15
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to build a metadata dataset for Export Dataflows
  -- Summary			:	The procedure accepts the following input parameters:

							@FlowID: The FlowID from [flw].[Ingestion]
							@FlowType: The flow type (default: 'exp')
							@ExecMode: Execution mode (default: 'man')
							@dbg: Debug level (default: 0)
							It fetches various configuration parameters and settings from the following tables:

							[flw].[BulkloadCFG]
							[flw].[SysCFG]
							[flw].[Export]
							[flw].[SysDataSource]
							[flw].[SysLog]
							
							The procedure then selects and displays the following values:

							Bulk load and general timeout settings
							Batch size, max retry attempts, and retry delay
							Source and target connection details
							Incremental column and date column
							File export settings (path, name, type, encoding, delimiter, etc.)
							Account and storage details
							Key Vault and secret names
							Next export date and value
							Timestamp and subfolder pattern settings
							Debug level

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-03-15		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetRVExport]
    @FlowID INT,                   -- FlowID from [flw].[Ingestion]
    @FlowType VARCHAR(50) = 'exp',
    @ExecMode VARCHAR(50) = 'man', -- Manual, Batch, Initial, Schedule
    @dbg INT = 0                   -- Show details. returns true/false if not
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
    DECLARE @MaxRetry INT =
            (
                SELECT TOP 1 [ParamValue] FROM flw.SysCFG WHERE [ParamName] = 'MaxRetry'
            );
    DECLARE @RetryDelayMS INT = [flw].[GetCFGParamVal]('RetryDelayMS');
    
   
    DECLARE @SysAlias [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @srcDBSchTblConnectionstring [NVARCHAR](250),
            @srcDBSchTbl [NVARCHAR](250),
            @IsSynapse BIT = 0,
            @IncrementalColumn NVARCHAR(255) = N'',
            @NoOfOverlapDays INT = 1,
            @DateColumn NVARCHAR(255) = N'',
            @FromDate NVARCHAR(255) = NULL,
            @ToDate NVARCHAR(255) = NULL,
            @ExportBy CHAR(1) = 'D',
            @ExportSize INT = 1,
            @trgPath [NVARCHAR](255) = N'',
            @trgFileName [NVARCHAR](250) = N'',
            @trgFiletype [NVARCHAR](250),
            @trgEncoding [NVARCHAR](250),
            @ColumnDelimiter CHAR(1) = '"',
            @TextQualifier CHAR(1) = ';',
            @ZipTrg BIT = 0,
            @OnErrorResume BIT = 1,
            
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

            @FileShare NVARCHAR(255) = N'',
            
            @NextExportDate NVARCHAR(255) = NULL,
            @NextExportValue INT = NULL,
            @AddTimeStampToFileName BIT = 1,
            @Subfolderpattern NVARCHAR(25) = N'',
            @srcWithHint NVARCHAR(255) = N'',
            @CompressionType NVARCHAR(25) = N'';

    --select CAST('1900-01-01' AS date)
    --[AddTimeStampToFileName]
    --[Subfolderpattern]

    SELECT @SysAlias = ex.SysAlias,
           @Batch = ex.Batch,
           @srcDBSchTblConnectionstring = ds.ConnectionString,
           @IsSynapse = ISNULL(ds.IsSynapse, 0),
           @srcDBSchTbl = ISNULL(ex.srcDBSchTbl, ''),
           @IncrementalColumn = ISNULL(ex.IncrementalColumn, ''),
           @NoOfOverlapDays = ISNULL(ex.NoOfOverlapDays, 1),
           @DateColumn = ISNULL(ex.DateColumn, ''),
           @FromDate = CASE
                           WHEN ex.FromDate IS NOT NULL THEN
                               FORMAT(ex.FromDate, 'yyyy-MM-dd')
                           WHEN ex.FromDate IS NULL
                                AND pLog.[NextExportDate] IS NULL THEN
                               FORMAT(CAST('1900-01-01' AS DATE), 'yyyy-MM-dd')
                           WHEN LEN(COALESCE(ex.DateColumn, ex.IncrementalColumn, '')) > 1 THEN
                               CAST(DATEADD(d, -1 * ISNULL(ex.NoOfOverlapDays, 1), GETDATE()) AS DATE)
                           ELSE
                               FORMAT(ISNULL(ex.FromDate, '1900-01-01'), 'yyyy-MM-dd')
                       END,
           @ToDate = CASE
                         WHEN ex.ToDate IS NOT NULL THEN
                             FORMAT(ex.ToDate, 'yyyy-MM-dd')
                         ELSE
                             FORMAT(ISNULL(ex.ToDate, CAST(GETDATE() AS DATE)), 'yyyy-MM-dd')
                     END,
           @ExportBy = ISNULL(ex.ExportBy, 'D'),
           @ExportSize = ISNULL(ex.ExportSize, 1),
           @trgPath = ISNULL(ex.trgPath, ''),
           @trgFileName = ISNULL(ex.trgFileName, ''),
           @trgFiletype = ISNULL(ex.trgFiletype, ''),
           @trgEncoding = ISNULL(ex.trgEncoding, ''),
           @ColumnDelimiter = ISNULL(ex.ColumnDelimiter, ';'),
           @TextQualifier = ISNULL(ex.TextQualifier, '"'),
           @NoOfThreads = CASE
                              WHEN ex.NoOfThreads > 0 THEN
                                  ex.NoOfThreads
                              ELSE
                                  @NoOfThreads
                          END,
           @ZipTrg = ISNULL(ZipTrg, 1),
           @OnErrorResume = ISNULL(OnErrorResume, 1),

		   @srcTenantId = ISNULL(skv1.TenantId, ''),
           @srcSubscriptionId = ISNULL(skv1.SubscriptionId, ''),
           @srcApplicationId = ISNULL(skv1.ApplicationId, ''),
           @srcClientSecret = ISNULL(skv1.ClientSecret, ''),
           @srcKeyVaultName = ISNULL(skv1.KeyVaultName, ''),
           @srcSecretName = ISNULL(ds.KeyVaultSecretName, ''),
           @srcResourceGroup = ISNULL(skv1.ResourceGroup, ''),
           @srcDataFactoryName = ISNULL(skv1.DataFactoryName, ''),
           @srcAutomationAccountName = ISNULL(skv1.AutomationAccountName, ''),
           @srcStorageAccountName = ISNULL(skv1.StorageAccountName, ''),
           @srcBlobContainer = ISNULL(skv1.BlobContainer, ''),

		   @trgTenantId = ISNULL(ss.TenantId, ''),
           @trgSubscriptionId = ISNULL(ss.SubscriptionId, ''),
           @trgApplicationId = ISNULL(ss.ApplicationId, ''),
           @trgClientSecret = ISNULL(ss.ClientSecret, ''),
           @trgKeyVaultName = ISNULL(ss.KeyVaultName, ''),
           @trgSecretName = '', -- Cant use SecretName for root access for the App Account
           @trgResourceGroup = ISNULL(ss.ResourceGroup, ''),
           @trgDataFactoryName = ISNULL(ss.DataFactoryName, ''),
           @trgAutomationAccountName = ISNULL(ss.AutomationAccountName, ''),
           @trgStorageAccountName = ISNULL(ss.StorageAccountName, ''),
           @trgBlobContainer = ISNULL(ss.BlobContainer, ''),

           @NextExportDate = ISNULL(pLog.[NextExportDate], '1900-01-01'),
           @NextExportValue = ISNULL(pLog.[NextExportValue], 0),
           @AddTimeStampToFileName = ISNULL([AddTimeStampToFileName], 0),
           @Subfolderpattern = ISNULL([Subfolderpattern], ''),
           @srcWithHint = ISNULL(srcWithHint, ''),
           @CompressionType = ISNULL(srcWithHint, 'Gzip')
    FROM [flw].[Export] ex
        INNER JOIN flw.SysDataSource ds
            ON ex.srcServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog
            ON ex.FlowID = pLog.[FlowID]
               AND ex.FlowType = pLog.[FlowType]
        LEFT OUTER JOIN [flw].[SysServicePrincipal] ss
            ON ex.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]

		LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
			ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
        
    WHERE ex.FlowID = @FlowID;
    --AND      EXP.FlowType    = 'EXP'


    IF (@srcDBSchTblConnectionstring IS NOT NULL)
    BEGIN

        SELECT @BulkLoadTimeoutInSek AS BulkLoadTimeoutInSek,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @BulkLoadBatchSize AS BulkLoadBatchSize,
               @MaxRetry AS MaxRetry,
               @RetryDelayMS AS RetryDelayMS,
               @IsSynapse AS srcIsSynapse,
               @SysAlias AS SysAlias,
               @Batch AS Batch,
               @srcDBSchTblConnectionstring AS srcConnectionString,
               @srcDBSchTbl AS srcDBSchTbl,
               @IncrementalColumn AS IncrementalColumn,
               @NoOfOverlapDays AS NoOfOverlapDays,
               @DateColumn AS DateColumn,
               @FromDate AS FromDate,
               @ToDate AS ToDate,
               @ExportBy AS ExportBy,
               @ExportSize AS ExportSize,
               @trgPath AS trgPath,
               @trgFileName AS trgFileName,
               @trgFiletype AS trgFiletype,
               @trgEncoding AS trgEncoding,
               @ColumnDelimiter AS ColumnDelimiter,
               @TextQualifier AS TextQualifier,
               @NoOfThreads AS NoOfThreads,
               @ZipTrg AS ZipTrg,
               @OnErrorResume AS OnErrorResume,
               
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

               @FileShare AS FileShare,
               @srcKeyVaultName AS srcKeyVaultName,
               @srcSecretName AS srcSecretName,
               @NextExportDate AS NextExportDate,
               @NextExportValue AS NextExportValue,
               @AddTimeStampToFileName AS AddTimeStampToFileName,
               @Subfolderpattern AS Subfolderpattern,
               @srcWithHint AS srcWithHint,
               @CompressionType AS CompressionType,
               @dbg AS dbg;

    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to build a metadata dataset for Export Dataflows
  -- Summary			:	The procedure accepts the following input parameters:

							@FlowID: The FlowID from [flw].[Ingestion]
							@FlowType: The flow type (default: ''exp'')
							@ExecMode: Execution mode (default: ''man'')
							@dbg: Debug level (default: 0)
							It fetches various configuration parameters and settings from the following tables:

							[flw].[BulkloadCFG]
							[flw].[SysCFG]
							[flw].[Export]
							[flw].[SysDataSource]
							[flw].[SysLog]
							[flw].[SysStorage]
							The procedure then selects and displays the following values:

							Bulk load and general timeout settings
							Batch size, max retry attempts, and retry delay
							Source and target connection details
							Incremental column and date column
							File export settings (path, name, type, encoding, delimiter, etc.)
							Account and storage details
							Key Vault and secret names
							Next export date and value
							Timestamp and subfolder pattern settings
							Debug level', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVExport', NULL, NULL
GO
