SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVPrePostSP]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure builds a metadata dataset for executing a stored procedure and its dynamic parameters. 
  -- Summary			:	The stored procedure starts by setting NOCOUNT ON and declaring several variables. It then retrieves the BulkLoadTimeoutInSek and 
							GeneralTimeoutInSek configuration parameter values based on the specified debugging level. The stored procedure also retrieves the dbgFilePath, 
							DbgFilePathAz, and DbgFilePathStorageAlias configuration parameter values, and the storage account name and key based on the specified DbgFilePathStorageAlias.

							The stored procedure retrieves the ConnectionString, KeyVaultName, SecretName, and IsSynapse values for the specified Flow ID from 
							the flw.PrePostSP and flw.SysDataSource tables. The stored procedure constructs the dbgFileName based on the FlowType, trgDBSchSP, and FlowID values.

							Finally, the stored procedure retrieves the parameter details for the specified Flow ID from the flw.PrePostSPParameter, flw.PrePostSP, 
							and flw.SysDataSource tables, and returns them.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetRVStoredProcedure]
    @FlowID INT,                      -- FlowID from [flw].[Ingestion]
    @FlowType VARCHAR(50) = 'ps',
    @ExecMode VARCHAR(50) = 'Manual', -- Manual, Batch, Initial, Schedule
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

    DECLARE @BulkLoadTimeoutInSek INT = [flw].[GetCFGParamVal]('BulkLoadTimeoutInSek');
    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');


    DECLARE @trgConString [NVARCHAR](250),
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
            @DbgFileName NVARCHAR(255) = N'',
            @trgDBSchSP NVARCHAR(255) = N'',
            @SysAlias [NVARCHAR](250) = N'',
            @OnErrorResume BIT = 1,
            @Batch NVARCHAR(255) = N'';

    SELECT @trgConString = ds.ConnectionString,
           @trgDBSchSP = ISNULL(ss.trgDBSchSP, ''),
           @SysAlias = SysAlias,
           @OnErrorResume = ISNULL(OnErrorResume, 1),
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
           @trgBlobContainer = ISNULL(skv1.BlobContainer, '')
    FROM flw.StoredProcedure ss
        INNER JOIN flw.SysDataSource ds
            ON ss.trgServer = ds.Alias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE ss.FlowID = @FlowID;

    --Set debug filename
    SET @DbgFileName
        = N'FLW_' + @FlowType + N'_' + [flw].[StrRemRegex](@trgDBSchSP, '%[^a-z]%') + N'_'
          + CAST(@FlowID AS VARCHAR(255)) + N'.txt';

    IF (@trgDBSchSP IS NOT NULL)
    BEGIN
        SELECT @trgDBSchSP AS trgDBSchSP,
               PARSENAME(@trgDBSchSP, 3) AS trgDatabase,
               PARSENAME(@trgDBSchSP, 2) AS trgSchema,
               PARSENAME(@trgDBSchSP, 1) AS trgObject,
               @trgConString AS trgConString,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @BulkLoadTimeoutInSek AS BulkLoadTimeoutInSek,
               @DbgFileName AS dbgFileName,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
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
               @dbg AS dbg;
    END;

    --Fetch SP Parameters
    SELECT P.ParameterID,
           P.FlowID,
           P.ParamAltServer,
           P.ParamName,
           P.SelectExp,
           P.PreFetch,
           P.Defaultvalue,
           ISNULL(dsAlt.SourceType, ds.SourceType) AS [SourceType],
           ISNULL(dsAlt.ConnectionString, ds.ConnectionString) AS ConnectionString,
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
        INNER JOIN flw.StoredProcedure sp
            ON P.FlowID = sp.FlowID
        LEFT OUTER JOIN flw.SysDataSource AS ds
            ON sp.trgServer = ds.Alias
        LEFT OUTER JOIN flw.SysDataSource AS dsAlt
            ON P.ParamAltServer = dsAlt.Alias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON dsAlt.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE P.FlowID = @FlowID;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure builds a metadata dataset for executing a stored procedure and its dynamic parameters. 
  -- Summary			:	The stored procedure starts by setting NOCOUNT ON and declaring several variables. It then retrieves the BulkLoadTimeoutInSek and 
							GeneralTimeoutInSek configuration parameter values based on the specified debugging level. The stored procedure also retrieves the dbgFilePath, 
							DbgFilePathAz, and DbgFilePathStorageAlias configuration parameter values, and the storage account name and key based on the specified DbgFilePathStorageAlias.

							The stored procedure retrieves the SQLConnectionString, KeyVaultName, SecretName, and IsSynapse values for the specified Flow ID from 
							the flw.PrePostSP and flw.SysDataSource tables. The stored procedure constructs the dbgFileName based on the FlowType, trgDBSchSP, and FlowID values.

							Finally, the stored procedure retrieves the parameter details for the specified Flow ID from the flw.PrePostSPParameter, flw.PrePostSP, 
							and flw.SysDataSource tables, and returns them.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVStoredProcedure', NULL, NULL
GO
