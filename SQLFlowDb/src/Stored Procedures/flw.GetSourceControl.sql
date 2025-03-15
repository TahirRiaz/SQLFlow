SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name             :   GetSourceControl
  -- Date             :   2022.12.21
  -- Author           :   Tahir Riaz
  -- Company          :   Business IQ
  -- Purpose          :   This stored procedure builds a metadata dataset for synchronization with source control repositories.
  -- Summary		  :   Takes two optional parameters, @SCAlias and @Batch. It returns the source control configuration details 
						  for a given SCAlias and/or Batch. The result set includes the source control ID, batch, SCAlias, server, database name, repository name, 
						  path to scripts, source control type, username, access token, SQL connection string, key vault name, secret name, and other relevant details.
						  The stored procedure joins tables flw.SysSourceControl, flw.SysSourceControlType, and flw.SysDataSource to obtain this information. The
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.12.21		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetSourceControl]
    @SCAlias NVARCHAR(70) = '', -- 
    @Batch NVARCHAR(50) = ''
AS
BEGIN

    SELECT sc.SourceControlID,
           sc.Batch,
           sc.SCAlias,
           sc.Server,
           sc.DBName,
           sc.RepoName,
           sc.ScriptToPath,
           sc.ScriptDataForTables,
           sct.SourceControlType,
           sct.Username,
           sct.AccessToken,
           ds.ConnectionString,
           ISNULL(skv1.TenantId, '') AS srcTenantId,
           ISNULL(skv1.SubscriptionId, '') AS srcSubscriptionId,
           ISNULL(skv1.ApplicationId, '') AS srcApplicationId,
           ISNULL(skv1.ClientSecret, '') AS srcClientSecret,
           ISNULL(skv1.KeyVaultName, '') AS srcKeyVaultName,
           ISNULL(ds.KeyVaultSecretName, '') AS srcSecretName,
           ISNULL(skv1.ResourceGroup, '') AS srcResourceGroup,
           ISNULL(skv1.DataFactoryName, '') AS srcDataFactoryName,
           ISNULL(skv1.AutomationAccountName, '') AS srcAutomationAccountName,
           ISNULL(skv1.StorageAccountName, '') AS srcStorageAccountName,
           ISNULL(skv1.BlobContainer, '') srcBlobContainer,
           ISNULL(skv1.TenantId, '') AS trgTenantId,
           ISNULL(skv1.SubscriptionId, '') AS trgSubscriptionId,
           ISNULL(skv1.ApplicationId, '') AS trgApplicationId,
           ISNULL(skv1.ClientSecret, '') AS trgClientSecret,
           ISNULL(skv1.KeyVaultName, '') AS trgKeyVaultName,
           COALESCE(sct.[ConsumerSecretName], sct.[AccessTokenSecretName], '') AS trgSecretName,
           ISNULL(skv1.ResourceGroup, '') AS trgResourceGroup,
           ISNULL(skv1.DataFactoryName, '') AS trgDataFactoryName,
           ISNULL(skv1.AutomationAccountName, '') AS trgAutomationAccountName,
           ISNULL(skv1.StorageAccountName, '') AS trgStorageAccountName,
           ISNULL(skv1.BlobContainer, '') trgBlobContainer,
           ds.IsSynapse,
           ISNULL(WorkSpaceName, '') AS WorkSpaceName,
           ISNULL(ProjectName, '') AS ProjectName,
           ISNULL([ProjectKey], '') AS [ProjectKey],
           ISNULL(ConsumerKey, '') AS ConsumerKey,
           ISNULL(ConsumerSecret, '') AS ConsumerSecret,
           ISNULL([CreateWrkProjRepo], 0) AS [CreateWrkProjRepo]
    FROM flw.SysSourceControl AS sc
        INNER JOIN flw.SysSourceControlType AS sct
            ON sc.SCAlias = sct.SCAlias
        INNER JOIN flw.SysDataSource AS ds
            ON sc.Server = ds.Alias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON skv2.[ServicePrincipalAlias] = sct.ServicePrincipalAlias
    WHERE sc.SCAlias = CASE
                           WHEN LEN(@SCAlias) > 0 THEN
                               @SCAlias
                           ELSE
                               sc.SCAlias
                       END
          AND Batch = CASE
                          WHEN LEN(@Batch) > 0 THEN
                              @Batch
                          ELSE
                              Batch
                      END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose          :   This stored procedure builds a metadata dataset for synchronization with source control repositories.
  -- Summary		  :   Takes two optional parameters, @SCAlias and @Batch. It returns the source control configuration details 
						  for a given SCAlias and/or Batch. The result set includes the source control ID, batch, SCAlias, server, database name, repository name, 
						  path to scripts, source control type, username, access token, SQL connection string, key vault name, secret name, and other relevant details.
						  The stored procedure joins tables flw.SysSourceControl, flw.SysSourceControlType, and flw.SysDataSource to obtain this information. The', 'SCHEMA', N'flw', 'PROCEDURE', N'GetSourceControl', NULL, NULL
GO
