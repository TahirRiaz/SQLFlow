SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE VIEW [flw].[ObjectDS]
AS
/*
SELECT      ds.FlowID,
			[FromObjectMK] AS ObjectMK,
			ds.trgDBSchSP AS ObjectName,
            s.[SourceType], s.[DatabaseName], s.[Alias], s.[ConnectionString], s.[KeyVaultName], s.[SecretName], s.[SupportsCrossDBRef], s.[IsSynapse], s.[IsLocal], mk.IsDependencyObject, 1 DS
  FROM      [flw].[PrePostSP] ds
 INNER JOIN [flw].[SysDataSource] s
    ON ds.trgServer = s.Alias
 LEFT OUTER JOIN [flw].[LineageObjectMK] mk
    ON mk.[ObjectMK] =ds.[FromObjectMK]
UNION
*/
SELECT ds.FlowID,
       ds.Batch,
       'sp' UsedAs,
       mk.[ObjectType],
       [ToObjectMK] AS ObjectMK,
       ds.trgDBSchSP AS ObjectName,
       PARSENAME(ds.trgDBSchSP, 3) AS [Database],
       PARSENAME(ds.trgDBSchSP, 2) AS [Schema],
       PARSENAME(ds.trgDBSchSP, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
       srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       2 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM flw.StoredProcedure ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.[ObjectMK] = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'src' UsedAs,
       mk.[ObjectType],
       [FromObjectMK],
       ds.srcDBSchTbl,
       PARSENAME(ds.srcDBSchTbl, 3) AS [Database],
       PARSENAME(ds.srcDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.srcDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
       
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
	   
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       3 DS,
       ds.SysAlias,
       CASE
           WHEN [FromObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Export] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.srcServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
       	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       4 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionJSN] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.[ObjectMK] = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -1),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       5 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionJSN] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       6 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXML] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.[ObjectMK] = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -2),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       7 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXML] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -3),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       8 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXML] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       9 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRQ] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -4),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       10 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRQ] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -5),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       11 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRQ] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       12 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXLS] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -6),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       13 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXLS] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -7),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       14 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionXLS] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       15 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionADO] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -8),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       16 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionADO] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       17 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionCSV] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -9),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       18 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionCSV] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -10),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       19 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionCSV] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'src' UsedAs,
       mk.[ObjectType],
       [FromObjectMK] AS ObjectMK,
       ds.srcDBSchTbl,
       PARSENAME(ds.srcDBSchTbl, 3) AS [Database],
       PARSENAME(ds.srcDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.srcDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       20 DS,
       ds.SysAlias,
       CASE
           WHEN [FromObjectMK] IS NULL THEN
               1
           WHEN [FromObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.srcServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[FromObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       21 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -11),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       22 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -12),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       23 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL
UNION
SELECT sk.FlowID,
       ds.Batch,
       'Skey' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -13),
       sk.[SurrogateDbSchTbl],
       PARSENAME(sk.[SurrogateDbSchTbl], 3) AS [Database],
       PARSENAME(sk.[SurrogateDbSchTbl], 2) AS [Schema],
       PARSENAME(sk.[SurrogateDbSchTbl], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       24 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[SurrogateKey] sk
        ON ds.FlowID = sk.FlowID
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.[ObjectMK] = sk.ToObjectMK
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT sk.FlowID,
       ds.Batch,
       'Mkey' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -14),
       [flw].[GetMKeyTrgName](ds.[trgDBSchTbl], [MatchKeyID]) AS [trgDBSchTbl],
       PARSENAME([flw].[GetMKeyTrgName](ds.[trgDBSchTbl], [MatchKeyID]), 3) AS [Database],
       PARSENAME([flw].[GetMKeyTrgName](ds.[trgDBSchTbl], [MatchKeyID]), 2) AS [Schema],
       PARSENAME([flw].[GetMKeyTrgName](ds.[trgDBSchTbl], [MatchKeyID]), 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       25 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[Ingestion] ds
    INNER JOIN [flw].[MatchKey] sk
        ON ds.FlowID = sk.FlowID
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.[ObjectMK] =sk.ToObjectMK
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'trg' UsedAs,
       mk.[ObjectType],
       [ToObjectMK],
       ds.trgDBSchTbl,
       PARSENAME(ds.trgDBSchTbl, 3) AS [Database],
       PARSENAME(ds.trgDBSchTbl, 2) AS [Schema],
       PARSENAME(ds.trgDBSchTbl, 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       26 DS,
       ds.SysAlias,
       CASE
           WHEN [ToObjectMK] IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRC] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectMK = ds.[ToObjectMK]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PreProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -15),
       ds.[PreProcessOnTrg],
       PARSENAME(ds.[PreProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PreProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PreProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       27 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRC] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PreProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE [PreProcessOnTrg] IS NOT NULL
UNION
SELECT ds.FlowID,
       ds.Batch,
       'PostProcess' UsedAs,
       mk.[ObjectType],
       ISNULL(mk.ObjectMK, -16),
       ds.[PostProcessOnTrg],
       PARSENAME(ds.[PostProcessOnTrg], 3) AS [Database],
       PARSENAME(ds.[PostProcessOnTrg], 2) AS [Schema],
       PARSENAME(ds.[PostProcessOnTrg], 1) AS [Object],
       s.[SourceType],
       s.DatabaseName AS DatabaseName,
       s.[Alias],
       s.ConnectionString AS ConnectionString,
 	   srcTenantId = ISNULL(skv.TenantId, ''),
       srcSubscriptionId = ISNULL(skv.SubscriptionId, ''),
       srcApplicationId = ISNULL(skv.ApplicationId, ''),
       srcClientSecret = ISNULL(skv.ClientSecret, ''),
       srcKeyVaultName = ISNULL(skv.KeyVaultName, ''),
       srcSecretName = ISNULL(s.KeyVaultSecretName, ''),
       srcResourceGroup = ISNULL(skv.ResourceGroup, ''),
       srcDataFactoryName = ISNULL(skv.DataFactoryName, ''),
       srcAutomationAccountName = ISNULL(skv.AutomationAccountName, ''),
       srcStorageAccountName = ISNULL(skv.StorageAccountName, ''),
       srcBlobContainer = ISNULL(skv.BlobContainer, ''),
       s.[SupportsCrossDBRef],
       s.[IsSynapse],
       s.[IsLocal],
       mk.IsDependencyObject,
       28 DS,
       ds.SysAlias,
       CASE
           WHEN mk.ObjectMK IS NULL THEN
               1
           WHEN ISNULL(mk.NotInUse, 0) = 0
                AND mk.[ObjectType] IN ( 'sp', 'tbl', 'vew', 'source' )
                AND LEN(ISNULL(mk.[ObjectDef], '')) = 0 THEN
               1
           ELSE
               0
       END NotProcessed,
       ActivityMonitoring
FROM [flw].[PreIngestionPRC] ds
    INNER JOIN [flw].[SysDataSource] s
        ON ds.trgServer = s.Alias
    LEFT OUTER JOIN [flw].[LineageObjectMK] mk
        ON mk.ObjectName = ds.[PostProcessOnTrg]
    LEFT OUTER JOIN [flw].[SysServicePrincipal] skv
        ON skv.[ServicePrincipalAlias] = s.ServicePrincipalAlias
WHERE ds.[PostProcessOnTrg] IS NOT NULL;
GO
