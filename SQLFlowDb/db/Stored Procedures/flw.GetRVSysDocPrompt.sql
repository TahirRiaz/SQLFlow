SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[GetRVSysDocPrompt]
    @ObjectName NVARCHAR(255) = '',
    @PromptName NVARCHAR(255) = ''
AS
BEGIN;

    WITH BASE
    AS (SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-config-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Description], '')) <= CASE
                                                          WHEN LEN(@ObjectName) > 0 THEN
                                                              LEN(ISNULL(d.[Description], ''))
                                                          ELSE
                                                              20
                                                      END
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-object-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Description], '')) <= CASE
                                                          WHEN LEN(@ObjectName) > 0 THEN
                                                              LEN(ISNULL(d.[Description], ''))
                                                          ELSE
                                                              20
                                                      END
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-internal-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Description], '')) <= CASE
                                                          WHEN LEN(@ObjectName) > 0 THEN
                                                              LEN(ISNULL(d.[Description], ''))
                                                          ELSE
                                                              20
                                                      END
              AND
              (
                  ObjectName IN
                  (
                      SELECT ObjectName FROM [flw].[GetInternalTableColumns]() --'[flw].[PreIngestionCSV]'
                  )
                  OR ObjectName IN
                     (
                         SELECT ObjectName FROM [flw].[GetInternalTables]() --'[flw].[PreIngestionCSV]'
                     )
              )
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-summary-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Summary], '')) <= CASE
                                                      WHEN LEN(@ObjectName) > 0 THEN
                                                          LEN(ISNULL(d.[Summary], ''))
                                                      ELSE
                                                          20
                                                  END
              AND
              (
                  ObjectName IN
                  (
                      SELECT ObjectName FROM [flw].[GetConfigTableColumns]()
                  )
                  OR ObjectName IN
                     (
                         SELECT ObjectName FROM [flw].[GetConfigTables]()
                     )
              )
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-static-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Description], '')) <= CASE
                                                          WHEN LEN(@ObjectName) > 0 THEN
                                                              LEN(ISNULL(d.[Description], ''))
                                                          ELSE
                                                              20
                                                      END
              AND
              (
                  ObjectName IN
                  (
                      SELECT ObjectName FROM [flw].[GetStaticTableColumns]() --'[flw].[PreIngestionCSV]'
                  )
                  OR ObjectName IN
                     (
                         SELECT ObjectName FROM [flw].[GetStaticTables]() --'[flw].[PreIngestionCSV]'
                     )
              )
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-question-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(d.[Question], '')) <= CASE
                                                       WHEN LEN(@ObjectName) > 0 THEN
                                                           LEN(ISNULL(d.[Question], ''))
                                                       ELSE
                                                           20
                                                   END
              AND ObjectName IN
                  (
                      SELECT ObjectName FROM [flw].[GetConfigTableColumns]() --'[flw].[PreIngestionCSV]'

                  )
        UNION ALL
        SELECT [PromptName],
               ObjectName,
               ObjectType,
               [flw].[GetSysDocParentCode](ObjectName) ParentCode,
               ObjectDef AS Code,
               [flw].[GetSysDocRelations](ObjectName) Relations,
               [flw].[GetSysDocDependsOn](ObjectName) AS DependsOn,
               [flw].[GetSysDocDependsOnBy](ObjectName) AS DependsOnBy,
               ISNULL(DescriptionOld, '') AS OldDescription,
               [flw].[GetSysDocNote](ObjectName) AS AdditionalInfo,
               COALESCE(a.[PayLoadJson], d.[PayLoadJson], '') AS [PayLoadJson],
               d.[Description],
               d.[Summary],
               [AccessKey],
               [SecretKey],
               [ServicePrincipalAlias],
               [TenantId],
               [SubscriptionId],
               [ApplicationId],
               [ClientSecret],
               [ResourceGroup],
               [KeyVaultName],
               [KeyVaultSecretName]
        FROM [flw].[SysDoc] d
            CROSS APPLY
        (
            SELECT TOP 1
                   [PromptName],
                   s.[PayLoadJson],
                   d.[Description],
                   d.[Summary],
                   [AccessKey],
                   [SecretKey],
                   k.[ServicePrincipalAlias],
                   [TenantId],
                   [SubscriptionId],
                   [ApplicationId],
                   [ClientSecret],
                   [ResourceGroup],
                   [KeyVaultName],
                   [KeyVaultSecretName]
            FROM [flw].[SysAIPrompt] s
                INNER JOIN [flw].[SysAPIKey] k
                    ON s.ApiKeyAlias = k.ApiKeyAlias
                LEFT OUTER JOIN [flw].[SysServicePrincipal] p
                    ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
            WHERE [PromptName] LIKE 'sqlflow-label-%'
                  AND CHARINDEX(ObjectType, [PromptName]) > 0
        ) a
        WHERE 1 = 1
              AND ObjectName = CASE
                                   WHEN LEN(@ObjectName) > 0 THEN
                                       @ObjectName
                                   ELSE
                                       ObjectName
                               END
              AND LEN(ISNULL(Label, '')) <= 2
              --CASE
              --                                     WHEN LEN(@ObjectName) > 0 THEN
              --                                         LEN(ISNULL(label, ''))
              --                                     ELSE
              --                                         2
              --                                 END
              AND
              (
                  ObjectName IN
                  (
                      SELECT QUOTENAME(TABLE_SCHEMA) + '.' + QUOTENAME(TABLE_NAME)
                      FROM INFORMATION_SCHEMA.TABLES
                      WHERE TABLE_TYPE = 'BASE TABLE'
                  )
                  OR ObjectName IN
                     (
                         SELECT QUOTENAME(col.TABLE_SCHEMA) + '.' + QUOTENAME(col.TABLE_NAME) + '.'
                                + QUOTENAME(col.COLUMN_NAME)
                         FROM INFORMATION_SCHEMA.COLUMNS col
                             INNER JOIN INFORMATION_SCHEMA.TABLES tbl
                                 ON col.TABLE_SCHEMA = tbl.TABLE_SCHEMA
                                    AND col.TABLE_NAME = tbl.TABLE_NAME
                         WHERE tbl.TABLE_TYPE = 'BASE TABLE'
                     )
              ))
    SELECT *
    FROM BASE
    WHERE BASE.PromptName = CASE
                                WHEN LEN(@PromptName) > 0 THEN
                                    @PromptName
                                ELSE
                                    BASE.PromptName
                            END
    ORDER BY ObjectName;
END;
GO
