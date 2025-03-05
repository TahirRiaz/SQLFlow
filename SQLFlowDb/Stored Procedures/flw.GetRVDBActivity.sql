SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[GetRVDBActivity]
    @FlowID INT = NULL,
    @node INT = NULL,
    @Batch VARCHAR(255) = NULL
AS
BEGIN

    --DECLARE @FlowID INT;
    --DECLARE @node INT = 1083;
    --DECLARE @Batch VARCHAR(255); --// = 'Citybike'

    IF (@FlowID IS NOT NULL)
    BEGIN
        SELECT DISTINCT
               [SourceType],
               DatabaseName,
               [Alias],
               [ConnectionString],
               [srcTenantId],
               [srcSubscriptionId],
               [srcApplicationId],
               [srcClientSecret],
               [srcKeyVaultName],
               [srcSecretName],
               [srcResourceGroup],
               [srcDataFactoryName],
               [srcAutomationAccountName],
               [srcStorageAccountName],
               [srcBlobContainer] [IsSynapse]
        FROM [flw].[ObjectDS]
        WHERE FlowID = @FlowID
              AND ISNULL(ActivityMonitoring, 0) = 1;
    END;
    ELSE IF @node IS NOT NULL
    BEGIN

        SELECT @Batch = Batch
        FROM [flw].[FlowDS]
        WHERE FlowID = @node;

        SELECT DISTINCT
               [SourceType],
               DatabaseName,
               [Alias],
               [ConnectionString],
               [srcTenantId],
               [srcSubscriptionId],
               [srcApplicationId],
               [srcClientSecret],
               [srcKeyVaultName],
               [srcSecretName],
               [srcResourceGroup],
               [srcDataFactoryName],
               [srcAutomationAccountName],
               [srcStorageAccountName],
               [srcBlobContainer],
               [IsSynapse],
               COUNT(*) NoOfObjects
        FROM [flw].[ObjectDS] o
            INNER JOIN [flw].[LineageMap] l
                ON o.FlowID = l.FlowID
        WHERE o.Batch = @Batch
              AND ISNULL(ActivityMonitoring, 0) = 1
        GROUP BY [SourceType],
                 DatabaseName,
                 [Alias],
                 [ConnectionString],
                 [srcTenantId],
                 [srcSubscriptionId],
                 [srcApplicationId],
                 [srcClientSecret],
                 [srcKeyVaultName],
                 [srcSecretName],
                 [srcResourceGroup],
                 [srcDataFactoryName],
                 [srcAutomationAccountName],
                 [srcStorageAccountName],
                 [srcBlobContainer],
                 [IsSynapse]
        ORDER BY NoOfObjects DESC;

    --   DECLARE @Step INT;

    --   SELECT @Step = Step
    --   FROM [flw].[LineageMap]
    --   WHERE FlowID = @node;

    --   SELECT DISTINCT
    --          [SourceType],
    --          DatabaseName,
    --          [Alias],
    --          [ConnectionString],
    --          [KeyVaultName],
    --          [SecretName],
    --          [IsSynapse],
    --	   Max(Level) AS MaxLevel,
    --	   COUNT(*) NoOfObjects
    --   FROM [flw].[ObjectDS] 
    --				o INNER JOIN 
    --	[flw].[LineageMap] l ON o.FlowID = l.FlowID
    --   WHERE o.FlowID IN
    --         (
    --             SELECT FlowID
    --             FROM [flw].[LineageMap] base
    --             WHERE base.RootObjectMK IN
    --                   (
    --                       SELECT RootObjectMK
    --                       FROM [flw].[LineageMap] sub
    --                       WHERE sub.RootObjectMK = base.RootObjectMK
    --                             AND FlowID = @node
    --                             AND sub.[Step] >= @Step
    --                   )
    --         )
    --GROUP BY [SourceType],
    --		   DatabaseName,
    --		   [Alias],
    --		   [ConnectionString],
    --		   [KeyVaultName],
    --		   [SecretName],
    --		   [IsSynapse]
    -- ORDER BY MaxLevel desc


    END;
    ELSE
    BEGIN

        SELECT DISTINCT
               [SourceType],
               DatabaseName,
               [Alias],
               [ConnectionString],
               [srcTenantId],
               [srcSubscriptionId],
               [srcApplicationId],
               [srcClientSecret],
               [srcKeyVaultName],
               [srcSecretName],
               [srcResourceGroup],
               [srcDataFactoryName],
               [srcAutomationAccountName],
               [srcStorageAccountName],
               [srcBlobContainer],
               [IsSynapse],
               COUNT(*) NoOfObjects
        FROM [flw].[ObjectDS] o
            INNER JOIN [flw].[LineageMap] l
                ON o.FlowID = l.FlowID
        WHERE 1 = 1
              AND o.Batch = CASE
                                WHEN LEN(ISNULL(@Batch, '')) > 1 THEN
                                    @Batch
                                ELSE
                                    o.Batch
                            END
              AND ISNULL(ActivityMonitoring, 0) = 1
        GROUP BY [SourceType],
                 DatabaseName,
                 [Alias],
                 [ConnectionString],
                 [srcTenantId],
                 [srcSubscriptionId],
                 [srcApplicationId],
                 [srcClientSecret],
                 [srcKeyVaultName],
                 [srcSecretName],
                 [srcResourceGroup],
                 [srcDataFactoryName],
                 [srcAutomationAccountName],
                 [srcStorageAccountName],
                 [srcBlobContainer],
                 [IsSynapse]
        ORDER BY NoOfObjects DESC;

    END;


END;


GO
