SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



--[flw].[ExecPreFlowXML] 1
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVInvoke]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this stored procedure [flw].[GetRVInvoke] is to configure and retrieve the necessary details for invoking a specific data flow based on its FlowID. 
							This stored procedure supports different invocation types like Azure Data Factory, Azure Automation Runbooks, and scripts. 
							It also handles debugging, error handling, and storage details.
  -- Summary			:	This stored procedure accepts the following input parameters:

							@FlowID: The FlowID for the specific data flow that needs to be invoked.
							@FlowType: The type of the flow, with a default value of 'ps'.
							@ExecMode: Execution mode, with a default value of 'Manual'.
							@dbg: Debug level, with a default value of 0.
							The stored procedure retrieves the relevant information related to the specified data flow, such as the invoke type, storage details,
							service principal information, and other necessary parameters. This information is then returned as a result set to be used 
							for invoking the data flow according to the specified configuration.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetRVInvoke]
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

    DECLARE @GeneralTimeoutInSek INT = [flw].[GetCFGParamVal]('GeneralTimeoutInSek');


    DECLARE @InvokeFile [NVARCHAR](250),
            @DbgFileName NVARCHAR(255) = N'',
            @Code NVARCHAR(MAX) = N'',
            @Arguments NVARCHAR(4000) = N'',
            @InvokeType NVARCHAR(255) = N'',
            @InvokePath NVARCHAR(255) = N'',
            @SysAlias [NVARCHAR](250) = N'',
            @Batch NVARCHAR(255) = N'',
            @OnErrorResume BIT = 1,
            @InvokeAlias NVARCHAR(255) = N'',
            @ServicePrincipalAlias NVARCHAR(255) = N'',
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
            @trgPipelineName NVARCHAR(255) = N'',
            @trgRunbookName NVARCHAR(255) = N'',

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
            @srcPipelineName NVARCHAR(255) = N'',
            @srcRunbookName NVARCHAR(255) = N'',


            @trgParameterJSON NVARCHAR(MAX) = N'';

    SELECT @InvokeFile = ss.InvokeFile,
           @Code = ss.Code,
           @Arguments = ss.Arguments,
           @InvokeType = ISNULL(ss.InvokeType, ''),
           @InvokeAlias = InvokeAlias,
           @InvokePath = InvokePath,
           @OnErrorResume = ISNULL(OnErrorResume, 1),
           @ServicePrincipalAlias = ISNULL(ss.trgServicePrincipalAlias, ''),
           @trgTenantId = ISNULL(sp1.TenantId, ''),
           @trgSubscriptionId = ISNULL(sp1.SubscriptionId, ''),
           @trgApplicationId = ISNULL(sp1.ApplicationId, ''),
           @trgClientSecret = ISNULL(sp1.ClientSecret, ''),
           @trgResourceGroup = ISNULL(sp1.ResourceGroup, ''),
           @trgDataFactoryName = ISNULL(sp1.DataFactoryName, ''),
           @trgPipelineName = ISNULL(ss.PipelineName, ''),
           @trgAutomationAccountName = ISNULL(sp1.AutomationAccountName, ''),
           @trgRunbookName = ISNULL(ss.RunbookName, ''),
           @trgParameterJSON = ISNULL(ParameterJSON, ''),
           @trgKeyVaultName = ISNULL(sp1.KeyVaultName, ''),
           @trgStorageAccountName = ISNULL(sp1.StorageAccountName, ''),
           @trgBlobContainer = ISNULL(sp1.BlobContainer, ''),
           @trgSecretName = N'',

           @srcTenantId = ISNULL(sp2.TenantId, ''),
           @srcSubscriptionId = ISNULL(sp2.SubscriptionId, ''),
           @srcApplicationId = ISNULL(sp2.ApplicationId, ''),
           @srcClientSecret = ISNULL(sp2.ClientSecret, ''),
           @srcResourceGroup = ISNULL(sp2.ResourceGroup, ''),
           @srcDataFactoryName = ISNULL(sp2.DataFactoryName, ''),
           @srcAutomationAccountName = ISNULL(sp2.AutomationAccountName, ''),
           @srcKeyVaultName = ISNULL(sp2.KeyVaultName, ''),
           @srcStorageAccountName = ISNULL(sp2.StorageAccountName, ''),
           @srcBlobContainer = ISNULL(sp2.BlobContainer, '')

    FROM flw.Invoke ss
        LEFT OUTER JOIN [flw].[SysServicePrincipal] sp1
            ON ss.trgServicePrincipalAlias = sp1.[ServicePrincipalAlias]
		LEFT OUTER JOIN [flw].[SysServicePrincipal] sp2
					ON ss.srcServicePrincipalAlias = sp2.[ServicePrincipalAlias]

    WHERE ss.FlowID = @FlowID;

    --Set debug filename
    SET @DbgFileName
        = N'FLW_' + @FlowType + N'_' + [flw].[StrRemRegex](@InvokeAlias, '%[^a-z]%') + N'_'
          + CAST(@FlowID AS VARCHAR(255)) + N'.txt';

    IF (@InvokeAlias IS NOT NULL)
    BEGIN
        SELECT @InvokeType AS InvokeType,
               @InvokeAlias AS InvokeAlias,
               @InvokePath AS InvokePath,
               @InvokeFile AS InvokeFile,
               @Code AS Code,
               @Arguments AS Arguments,
               @GeneralTimeoutInSek AS GeneralTimeoutInSek,
               @DbgFileName AS dbgFileName,
               @Batch AS Batch,
               @SysAlias AS SysAlias,
               @OnErrorResume AS onErrorResume,
               @ServicePrincipalAlias AS ServicePrincipalAlias,
               @trgTenantId AS trgTenantId,
               @trgSubscriptionId AS trgSubscriptionId,
               @trgApplicationId AS trgApplicationId,
               @trgClientSecret AS trgClientSecret,
               @trgResourceGroup AS trgResourceGroup,
               @trgDataFactoryName AS trgDataFactoryName,
               @trgPipelineName AS trgPipelineName,
               @trgAutomationAccountName AS trgAutomationAccountName,
               @trgRunbookName AS trgRunbookName,
               @trgParameterJSON AS trgParameterJSON,
               @trgKeyVaultName AS trgKeyVaultName,
               @trgSecretName AS trgSecretName,
               @trgStorageAccountName AS trgStorageAccountName,
               @trgBlobContainer AS trgBlobContainer,

			   @srcTenantId AS srcTenantId,
               @srcSubscriptionId AS srcSubscriptionId,
               @srcApplicationId AS srcApplicationId,
               @srcClientSecret AS srcClientSecret,
               @srcResourceGroup AS srcResourceGroup,
               @srcDataFactoryName AS srcDataFactoryName,
               @srcPipelineName AS srcPipelineName,
               @srcAutomationAccountName AS srcAutomationAccountName,
               @srcRunbookName AS srcRunbookName,
               @srcKeyVaultName AS srcKeyVaultName,
               @srcSecretName AS srcSecretName,
               @srcStorageAccountName AS srcStorageAccountName,
               @srcBlobContainer AS srcBlobContainer,


               @dbg AS dbg;
    END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this stored procedure [flw].[GetRVInvoke] is to configure and retrieve the necessary details for invoking a specific data flow based on its FlowID. 
							This stored procedure supports different invocation types like Azure Data Factory, Azure Automation Runbooks, and scripts. 
							It also handles debugging, error handling, and storage details.
  -- Summary			:	This stored procedure accepts the following input parameters:

							@FlowID: The FlowID for the specific data flow that needs to be invoked.
							@FlowType: The type of the flow, with a default value of ''ps''.
							@ExecMode: Execution mode, with a default value of ''Manual''.
							@dbg: Debug level, with a default value of 0.
							The stored procedure retrieves the relevant information related to the specified data flow, such as the invoke type, storage details,
							service principal information, and other necessary parameters. This information is then returned as a result set to be used 
							for invoking the data flow according to the specified configuration.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVInvoke', NULL, NULL
GO
