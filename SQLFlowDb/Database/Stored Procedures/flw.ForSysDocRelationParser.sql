SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE PROCEDURE [flw].[ForSysDocRelationParser]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT a.*
    FROM flw.Ingestion a
        INNER JOIN flw.Assertion sert
            ON sert.AssertionName = a.Assertions;


    SELECT p.*
    FROM flw.SysSubFolderPattern AS p
        INNER JOIN flw.Export AS e
            ON p.SubFolderPattern = e.Subfolderpattern
        INNER JOIN [flw].[SysFileEncoding] s
            ON e.trgEncoding = s.[Encoding]
        INNER JOIN [flw].[SysExportBy] b
            ON e.ExportBy = b.ExportBy
        INNER JOIN [flw].[SysCompressionType] c
            ON e.CompressionType = c.CompressionType;



    SELECT k.*
    FROM flw.SysAPIKey k
        INNER JOIN flw.SysAIPrompt
            ON k.ApiKeyAlias = flw.SysAIPrompt.ApiKeyAlias;



    SELECT c.*
    FROM flw.SysColumn c
        INNER JOIN flw.Ingestion
            ON c.ColumnName = flw.Ingestion.SysColumns;



    SELECT p.*
    FROM flw.SysServicePrincipal AS p
        INNER JOIN flw.SysAPIKey AS c
            ON p.ServicePrincipalAlias = c.ServicePrincipalAlias;




    SELECT d.*
    FROM flw.SysDoc d
        INNER JOIN flw.SysDocNote
            ON d.ObjectName = flw.SysDocNote.ObjectName;


    SELECT n.*
    FROM flw.SysFlowNote n
        INNER JOIN flw.FlowDS
            ON n.FlowID = flw.FlowDS.FlowID;


    SELECT csv.*
    FROM [flw].[PreIngestionXML] csv
        INNER JOIN flw.SysDataSource ds
            ON csv.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON csv.FlowID = pLog.[FlowID]
               AND csv.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON csv.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE csv.FlowID = 111;


    SELECT csv.*
    FROM [flw].[PreIngestionXLS] csv
        INNER JOIN flw.SysDataSource ds
            ON csv.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON csv.FlowID = pLog.[FlowID]
               AND csv.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON csv.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE csv.FlowID = 2323;


    SELECT prq.*
    FROM [flw].[PreIngestionPRQ] prq
        INNER JOIN flw.SysDataSource ds
            ON prq.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON prq.FlowID = pLog.[FlowID]
               AND prq.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON prq.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE prq.FlowID = 2323;


    SELECT PRC.*
    FROM [flw].[PreIngestionPRC] PRC
        INNER JOIN flw.SysDataSource ds
            ON PRC.trgServer = ds.Alias
        INNER JOIN flw.SysDataSource ds2
            ON PRC.srcServer = ds2.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON PRC.FlowID = pLog.[FlowID]
               AND PRC.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON PRC.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        INNER JOIN [flw].[SysServicePrincipal] skv2
            ON ds2.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE PRC.FlowID = 232;


    SELECT jsn.*
    FROM [flw].[PreIngestionJSN] jsn
        INNER JOIN flw.SysDataSource ds
            ON jsn.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON jsn.FlowID = pLog.[FlowID]
               AND jsn.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON jsn.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE jsn.FlowID = 112;


    SELECT csv.*
    FROM [flw].[PreIngestionCSV] csv
        INNER JOIN flw.SysDataSource ds
            ON csv.trgServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON csv.FlowID = pLog.[FlowID]
               AND csv.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON csv.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE csv.FlowID = 323;

    SELECT ss.*
    FROM flw.Invoke ss
        INNER JOIN [flw].[SysServicePrincipal] sp
            ON ss.trgServicePrincipalAlias = sp.[ServicePrincipalAlias]
    WHERE ss.FlowID = 2422;

    SELECT i.*
    FROM flw.Ingestion AS i
        INNER JOIN flw.SysDataSource AS ds
            ON i.trgServer = ds.Alias
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE (i.FlowID = 232);


    SELECT skey.*
    FROM [flw].[HealthCheck] skey
        INNER JOIN flw.Ingestion AS i
            ON i.FlowID = skey.FlowID
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE i.FlowID = 1;

	SELECT Mkey.*
    FROM flw.Ingestion AS i
           INNER JOIN [flw].[MatchKey] mkey ON i.FlowID = mkey.FlowID
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE i.FlowID = 1;



    SELECT b.*
    FROM flw.Ingestion b
        INNER JOIN flw.SysDataSource srcBDS
            ON srcBDS.Alias = b.srcServer
               OR srcBDS.DatabaseName = b.srcServer
               OR CAST(srcBDS.DataSourceID AS VARCHAR(255)) = b.srcServer
        INNER JOIN flw.SysDataSource trgBDS
            ON trgBDS.Alias = b.trgServer
               OR trgBDS.DatabaseName = b.trgServer
               OR CAST(trgBDS.DataSourceID AS VARCHAR(255)) = b.trgServer
        INNER JOIN [flw].[SysHashKeyType] hkType
            ON hkType.[HashKeyType] = b.[HashKeyType]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON srcBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        INNER JOIN [flw].[SysServicePrincipal] skv2
            ON trgBDS.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE b.FlowID = 2323;

    SELECT b.*
    FROM [flw].[PreIngestionADO] b
        INNER JOIN [flw].[SysLog] pLog WITH (NOLOCK)
            ON b.FlowID = pLog.[FlowID]
        INNER JOIN flw.SysDataSource srcBDS
            ON srcBDS.Alias = b.srcServer
        INNER JOIN flw.SysDataSource trgBDS
            ON trgBDS.Alias = b.trgServer
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON srcBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        INNER JOIN [flw].[SysServicePrincipal] skv2
            ON trgBDS.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE b.FlowID = 2323;


    SELECT ex.*
    FROM [flw].[Export] ex
        INNER JOIN flw.SysDataSource ds
            ON ex.srcServer = ds.Alias
        INNER JOIN [flw].[SysLog] pLog
            ON ex.FlowID = pLog.[FlowID]
               AND ex.FlowType = pLog.[FlowType]
        INNER JOIN [flw].[SysServicePrincipal] ss
            ON ex.[ServicePrincipalAlias] = ss.[ServicePrincipalAlias]
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON skv1.[ServicePrincipalAlias] = ds.ServicePrincipalAlias
    WHERE ex.FlowID = 434;


    SELECT asr.*
    FROM flw.Ingestion i
        INNER JOIN [flw].[Assertion] AS asr
            ON asr.[AssertionName] = i.Assertions
        INNER JOIN flw.SysDataSource AS trgBDS
            ON trgBDS.Alias = i.trgServer
        INNER JOIN [flw].[HealthCheck] AS skey
            ON i.FlowID = skey.FlowID
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON trgBDS.ServicePrincipalAlias = skv1.ServicePrincipalAlias
    WHERE i.FlowID = 3434
    ORDER BY i.FlowID;

    SELECT P.*
    FROM flw.[Parameter] AS P
        INNER JOIN flw.StoredProcedure sp
            ON P.FlowID = sp.FlowID
        INNER JOIN flw.SysDataSource AS ds
            ON sp.trgServer = ds.Alias
        INNER JOIN flw.SysDataSource AS dsAlt
            ON P.ParamAltServer = dsAlt.Alias
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        INNER JOIN [flw].[SysServicePrincipal] skv2
            ON dsAlt.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE P.FlowID = 333;

    SELECT P.*
    FROM flw.[Parameter] AS P
        INNER JOIN [flw].[PreIngestionPRQ] prq
            ON P.FlowID = prq.FlowID
        INNER JOIN flw.SysDataSource AS ds
            ON prq.trgServer = ds.Alias
        INNER JOIN flw.SysDataSource AS dsAlt
            ON P.ParamAltServer = dsAlt.Alias
        INNER JOIN [flw].[SysServicePrincipal] skv1
            ON ds.ServicePrincipalAlias = skv1.ServicePrincipalAlias
        LEFT OUTER JOIN [flw].[SysServicePrincipal] skv2
            ON dsAlt.ServicePrincipalAlias = skv2.ServicePrincipalAlias
    WHERE P.FlowID = 232;

    SELECT s.*
    FROM [flw].[SysAIPrompt] s
        INNER JOIN [flw].[SysAPIKey] k
            ON s.ApiKeyAlias = k.ApiKeyAlias
        INNER JOIN [flw].[SysServicePrincipal] p
            ON p.ServicePrincipalAlias = k.ServicePrincipalAlias
    WHERE [PromptName] LIKE 'sqlflow-object-%';

    SELECT a.*
    FROM flw.SysLog a
        INNER JOIN flw.SysStats s
            ON a.FlowID = s.FlowID
    WHERE a.FlowID = 232;

    SELECT flw.SysSourceControlType.*
    FROM flw.SysSourceControlType
        INNER JOIN flw.SysServicePrincipal
            ON flw.SysSourceControlType.ServicePrincipalAlias = flw.SysServicePrincipal.ServicePrincipalAlias;


    SELECT TOP (200)
           flw.SysSourceControlType.SourceControlTypeID,
           flw.SysSourceControlType.SourceControlType,
           flw.SysSourceControlType.ServicePrincipalAlias,
           flw.SysSourceControlType.SCAlias,
           flw.SysSourceControlType.Username,
           flw.SysSourceControlType.AccessToken,
           flw.SysSourceControlType.AccessTokenSecretName,
           flw.SysSourceControlType.ConsumerKey,
           flw.SysSourceControlType.ConsumerSecret,
           flw.SysSourceControlType.ConsumerSecretName,
           flw.SysSourceControlType.WorkSpaceName,
           flw.SysSourceControlType.ProjectName,
           flw.SysSourceControlType.ProjectKey,
           flw.SysSourceControlType.CreateWrkProjRepo
    FROM flw.SysSourceControlType
        INNER JOIN flw.SysSourceControl
            ON flw.SysSourceControlType.SCAlias = flw.SysSourceControl.SCAlias;



    SELECT TOP (200)
           flw.DataSubscriber.FlowID,
           flw.DataSubscriber.FlowType,
           flw.DataSubscriber.SubscriberType,
           flw.DataSubscriber.SubscriberName,
           flw.DataSubscriber.Batch,
           flw.DataSubscriber.FromObjectMK,
           flw.DataSubscriber.ToObjectMK,
           flw.DataSubscriber.CreatedBy,
           flw.DataSubscriber.CreatedDate
    FROM flw.DataSubscriber
        INNER JOIN flw.SysDataSubscriberType
            ON flw.DataSubscriber.SubscriberType = flw.SysDataSubscriberType.SubscriberType;


    SELECT TOP (200)
           flw.IngestionTokenExp.TokenExpAlias,
           flw.IngestionTokenExp.SelectExp,
           flw.IngestionTokenExp.SelectExpFull,
           flw.IngestionTokenExp.DataType,
           flw.IngestionTokenExp.Description,
           flw.IngestionTokenExp.Example,
           flw.IngestionTokenize.FlowID
    FROM flw.IngestionTokenExp
        INNER JOIN flw.IngestionTokenize
            ON flw.IngestionTokenExp.TokenExpAlias = flw.IngestionTokenize.TokenExpAlias;

END;
GO
