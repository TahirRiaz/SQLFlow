SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS OFF
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[Search]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is a global search for all information related to the registered data pipelines. 
							The output depends on the input, and providing a flowing yields detailed information about the flow.
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/

--[flw].[Search] 1205

CREATE procedure [flw].[Search]
    @ObjName nvarchar(255),
    @Lineage bit = 1
as
begin
    set nocount on;
    SET ANSI_WARNINGS OFF;
    --DECLARE @RelevantObjectNames NVARCHAR(4000) = N'',
    --        @FlowID              INT            = 0;

    --DECLARE @ObjName NVARCHAR(255) = 437;
    --Find Relevant FlowIDS
    IF (OBJECT_ID('tempdb..#FlowID') IS NOT NULL)
    BEGIN
        DROP TABLE #FlowID;
    END;

    --Show
    SELECT l.[FlowID]
    INTO #FlowID
    FROM flw.SysLog l
    WHERE l.FlowID = CASE
                         WHEN ISNUMERIC(@ObjName) = 1 THEN
                             @ObjName
                         ELSE
                             0
                     END;

    IF (@@ROWCOUNT = 0)
    BEGIN
        ; INSERT INTO #FlowID
          SELECT l.[FlowID]
          FROM flw.SysLog l
          WHERE (
                    [Process] LIKE '%' + @ObjName + '%'
                    OR [SelectCmd] LIKE '%' + @ObjName + '%'
                    OR [InsertCmd] LIKE '%' + @ObjName + '%'
                    OR [UpdateCmd] LIKE '%' + @ObjName + '%'
                    OR [DeleteCmd] LIKE '%' + @ObjName + '%'
                    OR [RuntimeCmd] LIKE '%' + @ObjName + '%'
                    OR [CreateCmd] LIKE '%' + @ObjName + '%'
                    OR [ErrorInsert] LIKE '%' + @ObjName + '%'
                    OR [ErrorUpdate] LIKE '%' + @ObjName + '%'
                    OR [ErrorDelete] LIKE '%' + @ObjName + '%'
                    OR [ErrorRuntime] LIKE '%' + @ObjName + '%'
                    OR l.SysAlias LIKE '%' + @ObjName + '%'
                    OR l.Batch LIKE '%' + @ObjName + '%'
                    OR l.[FromObjectDef] LIKE '%' + @ObjName + '%'
                    OR l.[ToObjectDef] LIKE '%' + @ObjName + '%'
                    OR l.[PreProcessOnTrgDef] LIKE '%' + @ObjName + '%'
                    OR l.[PostProcessOnTrgDef] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT l.[FlowID]
          FROM flw.SysLog l
          WHERE [FlowID] IN
                (
                    SELECT [FlowID]
                    FROM [flw].[PreIngestionTransfrom]
                    WHERE [SelectExp] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM flw.Ingestion
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcServer LIKE '%' + @ObjName + '%'
                    OR srcDBSchTbl LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR KeyColumns LIKE '%' + @ObjName + '%'
                    OR IncrementalColumns LIKE '%' + @ObjName + '%'
                    OR DateColumn LIKE '%' + @ObjName + '%'
                    OR DataSetColumn LIKE '%' + @ObjName + '%'
                    OR srcFilter LIKE '%' + @ObjName + '%'
                    OR IdentityColumn LIKE '%' + @ObjName + '%'
                    OR IgnoreColumns LIKE '%' + @ObjName + '%'
                    OR Description LIKE '%' + @ObjName + '%'
                    OR [srcFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM flw.PreIngestionCSV
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionXLS]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionPRQ]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionXLS]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionPRC]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionXML]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                    OR [preFilter] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[PreIngestionJSN]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR srcPath LIKE '%' + @ObjName + '%'
                    OR srcFile LIKE '%' + @ObjName + '%'
                    OR trgServer LIKE '%' + @ObjName + '%'
                    OR trgDBSchTbl LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR preFilter LIKE '%' + @ObjName + '%'
                    OR PreInvokeAlias LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[Export]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR [srcServer] LIKE '%' + @ObjName + '%'
                    OR [srcDBSchTbl] LIKE '%' + @ObjName + '%'
                    OR [srcFilter] LIKE '%' + @ObjName + '%'
                    OR [ServicePrincipalAlias] LIKE '%' + @ObjName + '%'
                    OR trgPath LIKE '%' + @ObjName + '%'
                    OR [trgFileName] LIKE '%' + @ObjName + '%'
                    OR [trgFiletype] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM [flw].[Invoke]
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR trgServicePrincipalAlias LIKE '%' + @ObjName + '%'
                    OR [InvokeAlias] LIKE '%' + @ObjName + '%'
                    OR [InvokePath] LIKE '%' + @ObjName + '%'
                    OR trgServicePrincipalAlias LIKE '%' + @ObjName + '%'
                    OR [PipelineName] LIKE '%' + @ObjName + '%'
                    OR [RunbookName] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT FlowID
          FROM flw.StoredProcedure
          WHERE (
                    SysAlias LIKE '%' + @ObjName + '%'
                    OR Batch LIKE '%' + @ObjName + '%'
                    OR [trgServer] LIKE '%' + @ObjName + '%'
                    OR [trgDBSchSP] LIKE '%' + @ObjName + '%'
                    OR [PostInvokeAlias] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT DISTINCT
                 FlowID
          FROM [flw].[SurrogateKey]
          WHERE (
                    [SurrogateDbSchTbl] LIKE '%' + @ObjName + '%'
                    OR [KeyColumns] LIKE '%' + @ObjName + '%'
                    OR [sKeyColumns] LIKE '%' + @ObjName + '%'
                    OR [SurrogateColumn] LIKE '%' + @ObjName + '%'
                    OR [PreProcess] LIKE '%' + @ObjName + '%'
                    OR [PostProcess] LIKE '%' + @ObjName + '%'
                )
          UNION
          SELECT DISTINCT
                 FlowID
          FROM [flw].[DataSubscriberDetails]
          WHERE (
                    [SubscriberName] LIKE '%' + @ObjName + '%'
                    OR [FullyQualifiedQuery] LIKE '%' + @ObjName + '%'
                );

    END;

    --Figure FlowType
    DECLARE @FlowType VARCHAR(25),
            @SysAlias VARCHAR(70),
            @Batch NVARCHAR(255) = N'',
            @FlowID INT;

    DECLARE @FlowCount INT = 0;
    SELECT @FlowCount = COUNT(*)
    FROM #FlowID;

    IF (@FlowCount = 0)
    BEGIN
        SELECT 'NoHits' AS DataSetName,
               'Your search - ' + @ObjName + ' - did not match any known objects.' AS Result;
    END;
    ELSE IF (@FlowCount > 1)
    BEGIN
        SELECT 'SysLog' AS DataSetName,
               a.[FlowID],
               a.FlowType,
               [Success],
               [Batch],
               [ProcessShort],
               '<A href="Search/' + CAST(a.[FlowID] AS VARCHAR(255)) + '">Details</A>' AS [Details],
               '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType) + CAST(a.[FlowID] AS VARCHAR(255))
               + '">Edit</A>' AS [Edit]
        FROM flw.SysLog a
        WHERE a.FlowID IN
              (
                  SELECT FlowID FROM #FlowID
              );
    END;


    --Singular Flow
    IF (@FlowCount = 1)
    BEGIN
        SELECT @FlowID = FlowID,
               @FlowType = ISNULL(FlowType, ''),
               @SysAlias = ISNULL(SysAlias, ''),
               @Batch = ISNULL(Batch, '')
        FROM [flw].[SysLog]
        WHERE FlowID IN
              (
                  SELECT FlowID FROM #FlowID
              );

        SELECT 'Exec' AS DataSetName,
               *
        FROM [flw].[GetTriggerDS](@FlowID);

        SELECT 'Log' AS DataSetName,
               CASE
                   WHEN [Success] = 1 THEN
                       '&#x2705;'
                   ELSE
                       '&#x274c;'
               END AS [Success],
               [Process],
               '<pre>' + TraceLog + '</pre>' AS TraceLog,
			   [ErrorInsert],
               [ErrorUpdate],
               [ErrorDelete],
               [ErrorRuntime],

               'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
               CASE
                   WHEN a.FlowType = 'ing' THEN
                       'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                   ELSE
                       ''
               END AS SetMinFromSrc,
               CASE
                   WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'exp', 'prq', 'jsn', 'prc' ) THEN
                       'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                   ELSE
                       ''
               END AS Reset,
               CASE
                   WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                       'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                   ELSE
                       ''
               END AS Files,
               '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType) + CAST(a.[FlowID] AS VARCHAR(255))
               + '">Edit Flow</A>' AS [EditFlow],
               '<A target="_blank" href="/sys-log/' + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit SysLog</A>' AS [Edit SysLog],
               
			   a.[FlowID],
               a.FlowType,
               [ExecMode],
               [Batch],
               [SysAlias],
               [ProcessShort],
               [StartTime],
               [EndTime],
               [DurationFlow],
               DurationFlowMax,
               DurationFlowAvg,
               [DurationPre],
               [DurationPost],
               [Fetched],
               [Inserted],
               [Updated],
               [Deleted],
               CAST(FlowRate AS VARCHAR(255)) + ' rows/sec' AS FlowRate,
               NoOfThreads,
               [FileDate],
               [FileDateHist],
               [FileSize],
               [NextExportDate],
               [NextExportValue],
               [ExecMode],
               [a].[FileName],
               [flw].[FormatScript]([SelectCmd]) AS [SelectCmd],
               '<pre>' + [InsertCmd] + '</pre>' AS [InsertCmd],
               '<pre>' + [UpdateCmd] + '</pre>' AS [UpdateCmd],
               '<pre>' + [DeleteCmd] + '</pre>' AS [DeleteCmd],
               '<pre>' + [RuntimeCmd] + '</pre>' AS [RuntimeCmd],
               [flw].[FormatScript]([CreateCmd]) AS [CreateCmd],
               '<pre>' + InferDatatypeCmd + '<pre>' AS [InferDatatypeCmd],
               
               [WhereIncExp],
               [WhereDateExp],
               [DataTypeWarning],
               [ColumnWarning]
        FROM flw.SysLog a
            LEFT OUTER JOIN
            (
                SELECT FlowID,
                       MAX(DurationFlow) AS DurationFlowMax,
                       MIN(DurationFlow) AS DurationFlowMin,
                       AVG(DurationFlow) AS DurationFlowAvg
                FROM flw.SysStats s
                GROUP BY FlowID
            ) b
                ON a.FlowID = b.FlowID
        WHERE a.FlowID IN
              (
                  SELECT FlowID FROM #FlowID
              );


        IF (@FlowType IN ( 'exp' ))
        BEGIN
            SELECT 'Export' AS DataSetName,
                   i.*
            FROM [flw].[Export] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  )
            ORDER BY FlowID;
        END;



        IF (@FlowType IN ( 'prq' ))
        BEGIN
            SELECT 'Parquet' AS DataSetName,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](FlowType) + CAST([FlowID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit],
                   [FlowID],
                   i.SysAlias,
                   [ServicePrincipalAlias],
                   [srcPath],
                   [srcPathMask],
                   [srcFile],
                   [SearchSubDirectories],
                   [copyToPath],
                   [srcDeleteIngested],
                   [srcDeleteAtPath],
                   [zipToPath],
                   [trgServer],
                   [trgDBSchTbl],
                   [preFilter],
                   [PartitionList],
                   [SyncSchema],
                   [ExpectedColumnCount],
                   [FetchDataTypes],
                   [OnErrorResume],
                   [NoOfThreads],
                   [PreProcessOnTrg],
                   [PostProcessOnTrg],
                   [InitFromFileDate],
                   [InitToFileDate],
                   [PreInvokeAlias],
                   [Batch],
                   [BatchOrderBy],
                   [DeactivateFromBatch],
                   [FlowType],
                   [FromObjectMK],
                   [ToObjectMK],
                   [ShowPathWithFileName],
                   [CreatedBy],
                   [CreatedDate]
            FROM [flw].[PreIngestionPRQ] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  )
            ORDER BY FlowID;


        END;
        IF (@FlowType IN ( 'prc' ))
        BEGIN
            SELECT 'Remote Procedure' AS DataSetName,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](i.FlowType)
                   + CAST(i.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   i.*,
                   s.[Owner],
                   s.[DomainExpert]
            FROM [flw].[PreIngestionPRC] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  )
            ORDER BY FlowID;


        END;
        IF (@FlowType IN ( 'ado' ))
        BEGIN
            SELECT 'PreIngestionADO' AS DataSetName,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](i.FlowType)
                   + CAST(i.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   i.*,
                   s.[Owner],
                   s.[DomainExpert]
            FROM [flw].[PreIngestionADO] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  )
            ORDER BY FlowID;

            SELECT 'Virtual Columns' AS DataSetName,
                   *
            FROM [flw].[PreIngestionADOVirtual]
            WHERE FlowID = @FlowID
            ORDER BY FlowID;

            SELECT 'Transformations' AS DataSetName,
                   *,
                   '<A target="_blank" href="/pre-ingestion-transfrom/' + +CAST([TransfromID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit]
            FROM [flw].[PreIngestionTransfrom] p
            WHERE FlowID = @FlowID;
        END;
        IF (@FlowType IN ( 'ing' ))
        BEGIN
            SELECT 'Ingestion' AS DataSetName,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](i.FlowType) + CAST([FlowID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit],
                   i.*,
                   s.[Owner],
                   s.[DomainExpert]
            FROM [flw].[Ingestion] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  )
            ORDER BY FlowID;

            SELECT 'SurrogateKey' AS DataSetName,
                   *,
                   '<A target="_blank" href="/surrogate-key/' + CAST([FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit]
            FROM [flw].[SurrogateKey]
            WHERE FlowID = @FlowID
            ORDER BY FlowID;

            SELECT 'Virtual Columns' AS DataSetName,
                   *
            FROM flw.IngestionVirtual
            WHERE FlowID = @FlowID
            ORDER BY FlowID;

            SELECT 'Transformations' AS DataSetName,
                   *
            FROM flw.IngestionTransfrom
            WHERE FlowID = @FlowID


            SELECT 'Tokenization' AS DataSetName,
                   t.FlowID,
                   t.ColumnName,
                   t.TokenExpAlias,
                   te.SelectExp,
                   te.SelectExpFull,
                   te.DataType,
                   te.Description
            FROM flw.IngestionTokenize AS t
                INNER JOIN flw.IngestionTokenExp AS te
                    ON t.TokenExpAlias = te.TokenExpAlias
            WHERE t.FlowID = @FlowID
            ORDER BY t.FlowID;

        END;
        IF (@FlowType IN ( 'csv' ))
        BEGIN
            SELECT 'PreIngestionCSV' AS DataSetName,
                   'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
                   CASE
                       WHEN a.FlowType = 'ing' THEN
                           'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                       ELSE
                           ''
                   END AS SetMinFromSrc,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Reset,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Files,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType)
                   + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   a.*
            FROM [flw].[PreIngestionCSV] a
            WHERE FlowID = @FlowID
            ORDER BY FlowID;
        END;
        IF (@FlowType IN ( 'xls' ))
        BEGIN
            SELECT 'PreIngestionXLS' AS DataSetName,
                   'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
                   CASE
                       WHEN a.FlowType = 'ing' THEN
                           'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                       ELSE
                           ''
                   END AS SetMinFromSrc,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Reset,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Files,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType)
                   + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   a.*
            FROM [flw].[PreIngestionXLS] a
            WHERE FlowID = @FlowID
            ORDER BY FlowID;
        END;
        IF (@FlowType IN ( 'prq' ))
        BEGIN
            SELECT 'PreIngestionPRQ' AS DataSetName,
                   'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
                   CASE
                       WHEN a.FlowType = 'ing' THEN
                           'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                       ELSE
                           ''
                   END AS SetMinFromSrc,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Reset,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Files,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType)
                   + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   a.*
            FROM [flw].[PreIngestionPRQ] a
            WHERE FlowID = @FlowID
            ORDER BY FlowID;
        END;
        IF (@FlowType IN ( 'xml' ))
        BEGIN
            SELECT 'PreIngestionXML' AS DataSetName,
                   'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
                   CASE
                       WHEN a.FlowType = 'ing' THEN
                           'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                       ELSE
                           ''
                   END AS SetMinFromSrc,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Reset,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Files,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType)
                   + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   a.*
            FROM [flw].[PreIngestionXML] a
            WHERE FlowID = @FlowID
            ORDER BY FlowID;

        END;
        IF (@FlowType IN ( 'jsn' ))
        BEGIN
            SELECT 'PreIngestionJSN' AS DataSetName,
                   'search ' + CAST(a.[FlowID] AS VARCHAR(255)) AS Search,
                   CASE
                       WHEN a.FlowType = 'ing' THEN
                           'SetMinFromSrc ' + CAST(a.[FlowID] AS VARCHAR(255)) + ',1'
                       ELSE
                           ''
                   END AS SetMinFromSrc,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'reset ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Reset,
                   CASE
                       WHEN a.FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ) THEN
                           'SearchFiles ' + CAST(a.[FlowID] AS VARCHAR(255))
                       ELSE
                           ''
                   END AS Files,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](a.FlowType)
                   + CAST(a.[FlowID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit],
                   a.*
            FROM [flw].[PreIngestionJSN] a
            WHERE FlowID = @FlowID
            ORDER BY FlowID;

        END;
        IF (@FlowType IN ( 'sub' ))
        BEGIN
            SELECT 'Subscriber' AS DataSetName,
                   [FlowID],
                   [FlowType],
                   [SubscriberName],
                   '<pre>' + STRING_AGG([FullyQualifiedQuery], ';') + '</pre>' AS SubscriberQueries
            FROM [flw].[DataSubscriberDetails]
            WHERE FlowID = @FlowID
            GROUP BY [FlowID],
                     [FlowType],
                     [SubscriberName],
                     [ToObjectMK];
        END;
        IF (@FlowType IN ( 'adf','cs', 'aut', 'inv' ))
        BEGIN
            SELECT 'Invoke' AS DataSetName,
                   'search ' + CAST([FlowID] AS VARCHAR(255)) AS Search,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl]('inv') + CAST([FlowID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit],
                   *
            FROM [flw].[Invoke]
            WHERE FlowID = @FlowID
            ORDER BY FlowID;
        END;
        IF (@FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ))
        BEGIN
            SELECT 'PreIngestionTransfrom' AS DataSetName,
                   TransfromID,
                   FlowID,
                   FlowType,
                   Virtual,
                   ColName,
                   ColAlias,
                   SelectExp,
                   '<A target="_blank" href="/pre-ingestion-transfrom/' + +CAST([TransfromID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit]
            FROM flw.PreIngestionTransfrom
            WHERE FlowID = @FlowID;
        END;
        IF (@FlowType IN ( 'sp' ))
        BEGIN
            SELECT 'StoredProcedure' AS DataSetName,
                   '<A target="_blank" href="' + [flw].[GetFlowTypeUiUrl](FlowType) + CAST([FlowID] AS VARCHAR(255))
                   + '">Edit</A>' AS [Edit],
                   *
            FROM flw.StoredProcedure
            WHERE FlowID = @FlowID
            ORDER BY FlowID;
        END;

        IF (@FlowType IN ( 'sp', 'prc' ))
        BEGIN
            SELECT 'Parameters' AS DataSetName,
                   *,
                   '<A target="_blank" href="/parameter/' + +CAST([ParameterID] AS VARCHAR(255)) + '">Edit</A>' AS [Edit]
            FROM [flw].[Parameter]
            WHERE FlowID IN
                  (
                      SELECT FlowID FROM #FlowID
                  );
        END;

        DECLARE @RelationObject NVARCHAR(255),
                @DupeRelation INT;

        SELECT @RelationObject = [trgDBSchObj]
        FROM [flw].[FlowDS]
        WHERE FlowID = @FlowID;
        WITH Rel
        AS (SELECT [LeftObject],
                   [LeftObjectCol],
                   [RightObject],
                   [RightObjectCol],
                   [ManualEntry]
            FROM [flw].[LineageObjectRelation]
            WHERE [LeftObject] = @RelationObject)
        SELECT 'Relation' AS DataSetName,
               *
        FROM Rel;


        SELECT TOP 5
               'Stats' AS DataSetName,
               FlowID,
               [ExecMode],
               StatsDate,
               StartTime,
               EndTime,
               DurationFlow AS DurFlow,
               DurationPre AS DurPre,
               DurationPost AS DurPost,
               Fetched,
               Inserted AS Ins,
               Updated AS Upd,
               Deleted AS Del,
               Success,
               CAST(FlowRate AS VARCHAR(255)) + ' rows/sec' FlowRate,
               NoOfThreads
        FROM flw.SysStats
        WHERE FlowID IN
              (
                  SELECT FlowID FROM #FlowID
              )
        ORDER BY StatsID DESC;


        SELECT @FlowID = FlowID
        FROM #FlowID;
        DECLARE @Columns AS NVARCHAR(MAX),
                @ColumnsWithUDF AS NVARCHAR(MAX),
                @Query AS NVARCHAR(MAX);

        -- Constructing the dynamic column list
        SELECT @Columns = STRING_AGG('[' + CombinedObjectName + ']', ', '),
               @ColumnsWithUDF
                   = STRING_AGG(
                                   ' [flw].[GetObjectDef]([' + CombinedObjectName + ']) as ObjectMK_'
                                   + CombinedObjectName,
                                   ', '
                               )
        FROM
        (
            SELECT DISTINCT
                   CAST(FromObjectMK AS NVARCHAR(255)) AS CombinedObjectName
            FROM [flw].[LineageMap] lm
            WHERE FlowID = @FlowID
            UNION
            SELECT DISTINCT
                   CAST(ToObjectMK AS NVARCHAR(255)) AS CombinedObjectName
            FROM [flw].[LineageMap] lm
            WHERE FlowID = @FlowID
        ) AS CombinedNames;

        -- Constructing the dynamic SQL
        SET @Query
            = N'WITH CombinedMKs AS (
			SELECT 
				lm.[FlowID], 
				CASE 
					WHEN type = ''From'' THEN  CAST(FromObjectMK AS NVARCHAR(255))
					ELSE  CAST(ToObjectMK AS NVARCHAR(255)) 
				END AS CombinedObjectName
			FROM [flw].[LineageMap] lm
			CROSS APPLY (VALUES (''From''), (''To'')) AS v(type)
			WHERE lm.flowID = ' + CAST(@FlowID AS VARCHAR(255))
              + N'
		)
		SELECT ''Scripts'' as DataSetName, [FlowID], ' + @ColumnsWithUDF
              + N'
		FROM CombinedMKs
		PIVOT
		(
			MAX(CombinedObjectName)
			FOR CombinedObjectName IN (' + @Columns + N')
		) AS PivotTable;';

        -- Execute the dynamic SQL
        EXEC sp_executesql @Query;


		SELECT 'Indexes' as DataSetName, [FlowID], '<pre>' + [trgDesiredIndex] + '</pre>' AS DesiredIndex, '<pre>' + [CurrentIndexes] + '</pre>' AS [CurrentIndexes]
		FROM [flw].[FlowDS] f
		INNER JOIN [flw].[LineageObjectMK] l ON f.ToObjectMK = l.ObjectMK
		WHERE FlowID =  @FlowID
		

        IF (@FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ))
        BEGIN
            SELECT TOP 30
                   'FileLog' AS DataSetName,
                   [BatchID],
                   [FlowID],
                   [FileDate_DW],
                   [FileName_DW],
                   [FileRowDate_DW],
                   [FileSize_DW],
                   [FileColumnCount],
                   '<A target="_blank" href="/set-file-date/' + CAST([FlowID] AS VARCHAR(255)) + '/' + [FileDate_DW]
                   + '">&#8680;</A>' AS [SetFileDate]
            FROM [flw].[SysLogFile]
            WHERE FlowID = @FlowID
            ORDER BY [FileRowDate_DW] DESC;

            SELECT DISTINCT TOP 5
                   'Invalid files' AS DataSetName,
                   [FileName_DW],
                   MAX([FileRowDate_DW]) AS [FileRowDate_DW],
                   [FileSize_DW],
                   [FileColumnCount],
                   [ExpectedColumnCount]
            FROM [flw].[SysLogFile]
            WHERE FlowID = @FlowID
                  AND [FileColumnCount] = 0
            GROUP BY [FileName_DW],
                     [FileSize_DW],
                     [FileColumnCount],
                     [ExpectedColumnCount]
            ORDER BY [FileRowDate_DW] DESC;
        END;


        DECLARE @SelectCmd NVARCHAR(MAX) = N'',
                @InsertCmd NVARCHAR(MAX) = N'',
                @UpdateCmd NVARCHAR(MAX) = N'',
                @DeleteCmd NVARCHAR(MAX) = N'',
                @RuntimeCmd NVARCHAR(MAX) = N'',
                @CreateCmd NVARCHAR(MAX) = N'',
                @ErrorInsert NVARCHAR(MAX) = N'',
                @ErrorUpdate NVARCHAR(MAX) = N'',
                @ErrorDelete NVARCHAR(MAX) = N'',
                @ErrorRuntime NVARCHAR(MAX) = N'',
                @GraphBefore NVARCHAR(MAX) = N'',
                @GraphAfter NVARCHAR(MAX) = N'',
                @LineageBefore NVARCHAR(MAX) = N'',
                @LineageAfter NVARCHAR(MAX) = N'',
                @ExecFlowCLR NVARCHAR(MAX) = N'',
                @ExecNode NVARCHAR(MAX) = N'',
                @ExecFlowPS NVARCHAR(MAX) = N'',
                @ExecFlowRunTimeValues NVARCHAR(MAX) = N'',
                @RelevantObjectIng NVARCHAR(4000) = N'',
                @RelevantObjectPre NVARCHAR(4000) = N'',
                @OwnershipPrint NVARCHAR(MAX) = N'',
                @CreatedBy NVARCHAR(MAX) = N'',
                @CreatedDate NVARCHAR(MAX) = N'',
                @Owner NVARCHAR(MAX) = N'',
                @DomainExp NVARCHAR(MAX) = N'';

        SELECT @SelectCmd = [SelectCmd],
               @InsertCmd = [InsertCmd],
               @UpdateCmd = [UpdateCmd],
               @DeleteCmd = [DeleteCmd],
               @RuntimeCmd = [RuntimeCmd],
               @CreateCmd = [CreateCmd],
               @ErrorInsert = [ErrorInsert],
               @ErrorUpdate = [ErrorUpdate],
               @ErrorDelete = [ErrorDelete],
               @ErrorRuntime = [ErrorRuntime]
        FROM flw.SysLog
        WHERE FlowID IN
              (
                  SELECT FlowID FROM #FlowID
              );

        SELECT @FlowID = FlowID
        FROM #FlowID;

        --SET @ExecFlowRunTimeValues
        --    = N'exec [flw].[GetRVPreFlow' + @FlowType + N'] @FlowID = ' + CAST(@FlowID AS VARCHAR(255))
        --      + N', @dbg = 1, @ExecMode = ''MAN'' ';

        -- Printing Created By, Created Date, Owner & DomainExpert
        SELECT @CreatedBy = ISNULL(i.CreatedBy, N''),
               @CreatedDate = ISNULL(i.CreatedDate, '1990-01-01'),
               @Owner = ISNULL(s.[Owner], N''),
               @DomainExp = ISNULL(s.DomainExpert, N'')
        FROM [flw].[Ingestion] i
            LEFT OUTER JOIN [flw].[SysAlias] s
                ON i.SysAlias = s.SysAlias
        WHERE i.FlowID = @FlowID;

        SET @OwnershipPrint
            = N'Created By:                ' + @CreatedBy + CHAR(13) + N'Created Date:              ' + @CreatedDate
              + CHAR(13) + N'System Owner:              ' + @Owner + CHAR(13) + N'Domain Expert:             '
              + @DomainExp;

        PRINT [flw].[GetLogHeader]('FLOW OWNERSHIP', @OwnershipPrint);

        -- Printing Statement for Execution Codes
        SET @ExecFlowCLR
            = N'GET FLOW RUNTIME VALUES:   exec [flw].[GetRV' + IIF(@FlowType = 'ing', '', 'Pre') + N'Flow' + @FlowType
              + N'] @FlowID = ' + CAST(@FlowID AS VARCHAR(255)) + N', @dbg = 1, @ExecMode = ''MAN'' ';
        PRINT [flw].[GetLogHeader]('Runtime Values', @ExecFlowCLR);

        IF @FlowType IN ( 'ing' )
        BEGIN
            SELECT @RelevantObjectIng
                = N' SELECT * FROM ' + SrcTable + CHAR(10) + CHAR(13) + N' SELECT * FROM ' + [Raw] + CHAR(10)
                  + CHAR(13) + N' SELECT * FROM ' + Trusted + CHAR(10) + CHAR(13) + N' SELECT * FROM ' + TrustedVersion
                  + CHAR(10) + CHAR(13) + N' SELECT * FROM ' + Temp + CHAR(10) + CHAR(13) + N' SELECT * FROM ' + Vault
                  + CHAR(10) + CHAR(13) + N' SELECT * FROM ' + VaultVersion
            FROM flw.GetCFGParam(@FlowID);
        END;
        -- SELECT *  FROM flw.GetCFGParam(240);
        -- Relavent Objects for PreIngestion
        DECLARE @SrcFile NVARCHAR(2000) = N'',
                @PreDBName NVARCHAR(55) = N'',
                @Schema07Pre NVARCHAR(55) = N'',
                @ObjectName NVARCHAR(200) = N'',
                @TrgTblPre NVARCHAR(2000) = N'';


        SELECT @SrcFile =
        (
            SELECT FileName FROM flw.SysLog WHERE FlowID = @FlowID
        ),
               @PreDBName = [flw].[GetWebApiUrl](),
               @Schema07Pre = flw.GetCFGParamVal('Schema07Pre'),
               @ObjectName = CASE
                                 WHEN @FlowType = 'XLS' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionXLS
            WHERE FlowID = @FlowID
        )
                                 WHEN @FlowType = 'PRQ' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionPRQ
            WHERE FlowID = @FlowID
        )
                                 WHEN @FlowType = 'PRC' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionPRC
            WHERE FlowID = @FlowID
        )
                                 WHEN @FlowType = 'JSN' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionJSN
            WHERE FlowID = @FlowID
        )
                                 WHEN @FlowType = 'CSV' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionCSV
            WHERE FlowID = @FlowID
        )
                                 WHEN @FlowType = 'XML' THEN
        (
            SELECT PARSENAME([trgDBSchTbl], 1)
            FROM flw.PreIngestionXML
            WHERE FlowID = @FlowID
        )
                             END,
               @TrgTblPre =
        (
            SELECT TOP 1
                   SUBSTRING(Process, CHARINDEX('trg.', Process) + 4, 200)
            FROM flw.SysLog
            WHERE FlowID = @FlowID
        );

        IF @FlowType IN ( 'XLS', 'CSV', 'XML', 'PRQ', 'jsn', 'prc' )
        BEGIN
            SELECT @RelevantObjectPre
                = N' Source File Name = ' + @SrcFile + CHAR(10) + CHAR(13) + N' SELECT * FROM [' + @PreDBName + N'].['
                  + @Schema07Pre + N'].[' + @ObjectName + N']' + CHAR(10) + CHAR(13) + N' SELECT * FROM [' + @PreDBName
                  + N'].[' + @Schema07Pre + N'].[v_' + @ObjectName + N']';
        END;

        SELECT @GraphBefore
            = N'exec [' + DB_NAME() + N'].[flw].[FlowGraph] @StartObject=' + CHAR(39)
              + CAST(ISNULL(@FlowID, 0) AS VARCHAR(255)) + CHAR(39)
              + N', @Expanded=2, @Dir=''B'', @MonoColor=0, @ShowFlowID=0 , @ShowObjectType=1',
               @GraphAfter
                   = N'exec [' + DB_NAME() + N'].[flw].[FlowGraph] @StartObject=' + CHAR(39)
                     + CAST(ISNULL(@FlowID, 0) AS VARCHAR(255)) + CHAR(39)
                     + N', @Expanded=2, @Dir=''A'', @MonoColor=0, @ShowFlowID=0, @ShowObjectType=1',
               @LineageBefore
                   = N'select * from  [' + DB_NAME() + N'].[flw].[LineageBefore] (' + CHAR(39)
                     + CAST(ISNULL(@FlowID, 0) AS VARCHAR(255)) + CHAR(39) + N',0)',
               @LineageAfter
                   = N'select * from  [' + DB_NAME() + N'].[flw].[LineageAfter] (' + CHAR(39)
                     + CAST(ISNULL(@FlowID, 0) AS VARCHAR(255)) + CHAR(39) + N',0)';

        IF @ErrorInsert IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@ErrorInsert', @ErrorInsert);
        END;
        IF @ErrorUpdate IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@ErrorUpdate', @ErrorUpdate);
        END;
        IF @ErrorDelete IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@ErrorDelete', @ErrorDelete);
        END;
        IF @ErrorRuntime IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@ErrorRuntime', @ErrorRuntime);
        END;
        IF @SelectCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@SelectCmd', @SelectCmd);
        END;
        IF @InsertCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@InsertCmd', @InsertCmd);
        END;
        IF @UpdateCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@UpdateCmd', @UpdateCmd);
        END;
        IF @DeleteCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@DeleteCmd', @DeleteCmd);
        END;
        IF @CreateCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@CreateCmd', @CreateCmd);
        END;
        IF @RuntimeCmd IS NOT NULL
        BEGIN
            PRINT [flw].[GetLogHeader]('@RuntimeCmd', @RuntimeCmd);
        END;
        PRINT [flw].[GetLogHeader]('@GraphBefore', @GraphBefore);
        PRINT [flw].[GetLogHeader]('@GraphAfter', @GraphAfter);
        PRINT [flw].[GetLogHeader]('@LineageBefore', @LineageBefore);
        PRINT [flw].[GetLogHeader]('@LineageAfter', @LineageAfter);

        IF @FlowType IN ( 'ing' )
        BEGIN
            PRINT [flw].[GetLogHeader]('Relevant Objects', @RelevantObjectIng);
        END;

        IF @FlowType IN ( 'XLS', 'CSV', 'XML', 'PRQ', 'JSN', 'prc' )
        BEGIN
            PRINT [flw].[GetLogHeader]('Relevant Objects', @RelevantObjectPre);
        END;



    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is a global search for all information related to the registered data pipelines. 
							The output depends on the input, and providing a flowing yields detailed information about the flow.', 'SCHEMA', N'flw', 'PROCEDURE', N'Search', NULL, NULL
GO
