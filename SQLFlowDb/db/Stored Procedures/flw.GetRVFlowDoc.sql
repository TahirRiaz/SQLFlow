SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS OFF
GO

CREATE PROCEDURE [flw].[GetRVFlowDoc]
AS
BEGIN
    --[flw].[Search] 1205
    -- 1263
    DECLARE @FlowID INT = 1049; --1205 1049; --1049

    BEGIN
        SET NOCOUNT ON;
        SET ANSI_WARNINGS OFF;
        --DECLARE @RelevantObjectNames NVARCHAR(4000) = N'',
        --        @FlowID              INT            = 0;

        --DECLARE @ObjName NVARCHAR(255) = 437;
        --Find Relevant FlowIDS

        DECLARE @SelectCmd NVARCHAR(MAX) = N'',
                @InsertCmd NVARCHAR(MAX) = N'',
                @UpdateCmd NVARCHAR(MAX) = N'',
                @DeleteCmd NVARCHAR(MAX) = N'',
                @RuntimeCmd NVARCHAR(MAX) = N'',
                @CreateCmd NVARCHAR(MAX) = N'',
                @TraceLog NVARCHAR(MAX) = N'';


        SELECT @SelectCmd = ISNULL('`' + [SelectCmd] + '`', ''),
               @InsertCmd = ISNULL('`' + [InsertCmd] + '`', ''),
               @UpdateCmd = ISNULL('`' + [UpdateCmd] + '`', ''),
               @DeleteCmd = ISNULL('`' + [DeleteCmd] + '`', ''),
               @RuntimeCmd = ISNULL('`' + [RuntimeCmd] + '`', ''),
               @CreateCmd = ISNULL('`' + [CreateCmd] + '`', ''),
               @TraceLog = ISNULL('`' + [TraceLog] + '`', '')
        FROM flw.SysLog
        WHERE FlowID = @FlowID;


        --Figure FlowType
        DECLARE @FlowType VARCHAR(25),
                @SysAlias VARCHAR(70),
                @Dupe INT = 0,
                @Batch NVARCHAR(255) = N'';

        SELECT @FlowType = ISNULL(FlowType, ''),
               @SysAlias = ISNULL(SysAlias, ''),
               @Batch = ISNULL(Batch, '')
        FROM [flw].[SysLog]
        WHERE FlowID = @FlowID;

        IF OBJECT_ID('tempdb..#PipelineSourceObject') IS NOT NULL
        BEGIN
            DROP TABLE #PipelineSourceObject;
        END;

        SELECT lo.ObjectName AS ObjectName,
               [ObjectType],
               Step,
               ISNULL('`' + lo.ObjectDef + '`', '') AS SchemaCreatedBySQLFlow
        INTO #PipelineSourceObject
        FROM [flw].[LineageMap] lm
            INNER JOIN [flw].[LineageObjectMK] lo
                ON lm.FromObjectMK = lo.ObjectMK
        WHERE FlowID = @FlowID;

        SELECT '#PipelineSourceObject' AS DataSetName,
               ObjectName,
               MAX([ObjectType]) AS [ObjectType],
               MAX(Step) AS Step,
               MAX(SchemaCreatedBySQLFlow) AS SchemaCreatedBySQLFlow
        FROM #PipelineSourceObject
        GROUP BY ObjectName
        ORDER BY Step;

        IF EXISTS
        (
            SELECT 1
            FROM [flw].[SysFlowNote]
            WHERE FlowNoteType = 'Info'
                  AND FlowID = @FlowID
        )
        BEGIN
            SELECT '##PipelineAnnotations' AS DataSetName,
                   STRING_AGG([Description], CHAR(13) + CHAR(10)) [Description]
            FROM [flw].[SysFlowNote]
            WHERE FlowNoteType = 'Info'
                  AND FlowID = @FlowID;
        END;




        IF (LEN(@SelectCmd) > 0)
        BEGIN
            SELECT '##QueryAgainstSourceObject' AS DataSetName,
                   @SelectCmd Query;
        END;

        IF (LEN(@TraceLog) > 0)
        BEGIN
            SELECT '##PipeLineTraceLogLastExecution' AS DataSetName,
                   @TraceLog TraceLog;
        END;

        --SELECT 'PipelineSourceObject' AS DataSetName,
        --          ObjectName,
        --	   --Step,
        --          [ObjectType],
        --          GeneratedObjectBySQLFlow
        --FROM #SourceTarget
        --WHERE ObjectFunction = 'Source'

        --Figure The Parent Table
        IF (@FlowType IN ( 'exp' ))
        BEGIN
            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##ExportTable', '[flw].[Export]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##ExportTableColumns', 'Export', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##ExportTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[Export] i
            WHERE FlowID = @FlowID;

        END;
        IF (@FlowType IN ( 'prq' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionPrqTable', '[flw].[PreIngestionPRQ]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionPrqTableColumns', 'PreIngestionPRQ', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionPrqTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionPRQ] i
            WHERE FlowID = @FlowID;



        END;
        IF (@FlowType IN ( 'prc' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionPrcTable', '[flw].[PreIngestionPRC]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionPrcTableColumns', 'PreIngestionPRC', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionPrcTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionPRC] i
                LEFT OUTER JOIN [flw].[SysAlias] s
                    ON i.SysAlias = s.SysAlias
            WHERE FlowID = @FlowID;


            IF EXISTS (SELECT 1 FROM [flw].[Parameter] i WHERE FlowID = @FlowID)
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##ParametersTable', '[flw].[Parameter]', 0) s;

                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##ParametersTableColumns', 'Parameter', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##ParametersTableColumnValues' AS DataSetName,
                       i.*
                FROM [flw].[Parameter] i
                WHERE FlowID = @FlowID;
            END;
        END;
        IF (@FlowType IN ( 'ado' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionAdoTable', '[flw].[PreIngestionADO]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionAdoTableColumns', 'PreIngestionADO', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;


            SELECT '##PreIngestionAdoTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionADO] i
            WHERE FlowID = @FlowID;



            IF EXISTS
            (
                SELECT 1
                FROM [flw].[PreIngestionADOVirtual] i
                WHERE FlowID = @FlowID
            )
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##PreIngestionAdoVirtualTable', '[flw].[PreIngestionADOVirtual]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##PreIngestionAdoVirtualTableColumns', 'PreIngestionADOVirtual', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##PreIngestionAdoVirtualTableColumnValues' AS DataSetName,
                       *
                FROM [flw].[PreIngestionADOVirtual]
                WHERE FlowID = @FlowID;
            END;



        END;
        IF (@FlowType IN ( 'ing' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##IngestionTable', '[flw].[Ingestion]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##IngestionTableColumns', 'Ingestion', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;


            SELECT '##IngestionTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[Ingestion] i
            WHERE FlowID = @FlowID;

            IF EXISTS (SELECT 1 FROM [flw].[SurrogateKey] i WHERE FlowID = @FlowID)
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##SurrogateKeyTable', '[flw].[SurrogateKey]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##SurrogateKeyTableColumns', 'SurrogateKey', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##SurrogateKeyTableColumnValues' AS DataSetName,
                       *
                FROM [flw].[SurrogateKey]
                WHERE FlowID = @FlowID;
            END;


			IF EXISTS (SELECT 1 FROM [flw].[SurrogateKey] i WHERE FlowID = @FlowID)
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##MatchKeyTable', '[flw].[MatchKey]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##MatchKeyTableColumns', 'MatchKey', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##MatchKeyTableColumnValues' AS DataSetName,
                       *
                FROM [flw].[MatchKey]
                WHERE FlowID = @FlowID;
            END;


            IF EXISTS (SELECT 1 FROM flw.IngestionVirtual i WHERE FlowID = @FlowID)
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##IngestionVirtuallTable', '[flw].[IngestionVirtual]', 0) s;

                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##IngestionVirtuallTableColumns', 'IngestionVirtual', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##IngestionVirtuallTableColumnValues' AS DataSetName,
                       *
                FROM flw.IngestionVirtual
                WHERE FlowID = @FlowID;
            END;

            --REMOVE This Feature
            IF EXISTS
            (
                SELECT 1
                FROM flw.IngestionTransfrom i
                WHERE FlowID = @FlowID
            )
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##IngestionTransfromTable', '[flw].[IngestionTransfrom]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##IngestionTransfromTableColumns', 'IngestionTransfrom', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##IngestionTransfromTableColumnValues' AS DataSetName,
                       [FlowID], [ColumnName], 
					   '`' + [DataTypeExp] + '`' AS [DataTypeExp], 
                       '`' + [SelectExp] + '`' AS [SelectExp]
                FROM flw.IngestionTransfrom i
                WHERE FlowID = @FlowID
            END;


            IF EXISTS
            (
                SELECT 1
                FROM flw.IngestionTokenize AS t
                    INNER JOIN flw.IngestionTokenExp AS te
                        ON t.TokenExpAlias = te.TokenExpAlias
                WHERE t.FlowID = @FlowID
            )
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##IngestionTokenizeTable', '[flw].[IngestionTokenize]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##IngestionTokenizeTableColumns', 'IngestionTokenize', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;


                SELECT '##IngestionTokenizeTableColumnValues' AS DataSetName,
                       [FlowID],
                       [ColumnName],
                       t.[TokenExpAlias]
                FROM flw.IngestionTokenize AS t
                    INNER JOIN flw.IngestionTokenExp AS te
                        ON t.TokenExpAlias = te.TokenExpAlias
                WHERE t.FlowID = @FlowID
                ORDER BY t.FlowID;

                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##IngestionTokenExpTable', '[flw].[IngestionTokenExp]', 0) s;


                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##IngestionTokenExpTableColumns', 'IngestionTokenExp', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##IngestionTokenExpTableColumnValues' AS DataSetName,
                       te.[TokenExpAlias],
                       '`' + te.[SelectExp] + '`' AS [TokenSelectExp],
                       te.[SelectExpFull],
                       te.[DataType],
                       te.[Description],
                       te.[Example]
                FROM flw.IngestionTokenize AS t
                    INNER JOIN flw.IngestionTokenExp AS te
                        ON t.TokenExpAlias = te.TokenExpAlias
                WHERE t.FlowID = @FlowID
                ORDER BY t.FlowID;
            END;





        END;
        IF (@FlowType IN ( 'csv' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionCsvTable', '[flw].[PreIngestionCSV]', 1) s;


            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionCsvTableColumns', 'PreIngestionCSV', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionCsvTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionCSV] i
            WHERE FlowID = @FlowID;

        END;
        IF (@FlowType IN ( 'xls' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionXlsTable', '[flw].[PreIngestionXLS]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionXlsTableColumns', 'PreIngestionXLS', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionXlsTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionXLS] i
            WHERE FlowID = @FlowID;

        END;
        IF (@FlowType IN ( 'prq' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionPrqTable', '[flw].[PreIngestionPRQ]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionPrqTableColumns', 'PreIngestionPRQ', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionPrqTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionPRQ] i
            WHERE FlowID = @FlowID;

        END;
        IF (@FlowType IN ( 'xml' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionXmlTable', '[flw].[PreIngestionXML]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionXmlTableColumns', 'PreIngestionXML', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionXmlTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionXML] i
            WHERE FlowID = @FlowID;


        END;
        IF (@FlowType IN ( 'jsn' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionJsnTable', '[flw].[PreIngestionJSN]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionJsnTableColumns', 'PreIngestionJSN', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##PreIngestionJsnTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[PreIngestionJSN] i
            WHERE FlowID = @FlowID;


        END;
        IF (@FlowType IN ( 'sub' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##DataSubscriberTable', '[flw].[DataSubscriber]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##DataSubscriberTableColumns', 'DataSubscriber', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##DataSubscriberTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[DataSubscriber] i
            WHERE FlowID = @FlowID;


            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##DataSubscriberQueryTable', '[flw].[DataSubscriberQuery]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##DataSubscriberQueryTableColumns', 'DataSubscriberQuery', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##DataSubscriberQueryTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[DataSubscriberQuery] i
                INNER JOIN [flw].[DataSubscriber] d
                    ON i.FlowID = d.FlowID
            WHERE d.FlowID = @FlowID;


        END;
        IF (@FlowType IN ( 'sp' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##StoredProcedureTable', '[flw].[StoredProcedure]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##StoredProcedureTableColumns', 'StoredProcedure', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##StoredProcedureTableColumnValues' AS DataSetName,
                   i.*
            FROM flw.StoredProcedure i
            WHERE FlowID = @FlowID;

            IF EXISTS (SELECT 1 FROM [flw].[Parameter] i WHERE FlowID = @FlowID)
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##Parameters', '[flw].[Parameter]', 0) s;

                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##ParametersColumns', 'Parameter', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;

                SELECT '##ParametersValues' AS DataSetName,
                       i.*
                FROM [flw].[Parameter] i
                WHERE FlowID = @FlowID;
            END;
        END;
        IF (@FlowType IN ( 'adf', 'aut', 'bat', 'inv','cs' ))
        BEGIN

            SELECT DataSetName,
                   DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##MetaDataTable', '[flw].[Invoke]', 1) s;

            SELECT DataSetName,
                   s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##MetaDataTableColumns', 'Invoke', 1) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;

            SELECT '##MetaDataTableColumnValues' AS DataSetName,
                   i.*
            FROM [flw].[Invoke] i
            WHERE FlowID = @FlowID;

        END;

        IF (@FlowType IN ( 'csv', 'xml', 'xls', 'prq', 'jsn', 'prc' ))
        BEGIN

            IF EXISTS
            (
                SELECT 1
                FROM flw.PreIngestionTransfrom i
                WHERE FlowID = @FlowID
            )
            BEGIN
                SELECT DataSetName,
                       DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTable]('##PreIngestionTransfrom', '[flw].[PreIngestionTransfrom]', 0) s;

                SELECT DataSetName,
                       --s.[ObjectName],
                       [Documentation],
                       [ObjectDef]
                FROM [flw].[GetSysDocTableColumns]('##PreIngestionTransfromColumns', 'PreIngestionTransfrom', 0) s
                    INNER JOIN
                    (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                        ON s.[ObjectName] = a.[ObjectName]
                ORDER BY a.ORDINAL_POSITION;


                SELECT '##PreIngestionTransfromValues' AS DataSetName,
                       [FlowID],
                       [FlowType],
                       [Virtual],
                       [ColName] AS [SourceColName],
                       '`' + [SelectExp] + '`' AS [TransFormExp],
                       [ColAlias],
                       [ExcludeColFromView]
                FROM flw.PreIngestionTransfrom
                WHERE FlowID = @FlowID;
            END;

            SELECT DataSetName,
                   DataSetName,
                   --s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTable]('##PreIngestionTransfrom', '[flw].[PreIngestionTransfrom]', 0) s;

            SELECT DataSetName,
                   --s.[ObjectName],
                   [Documentation],
                   [ObjectDef]
            FROM [flw].[GetSysDocTableColumns]('##PreIngestionTransfromColumns', 'PreIngestionTransfrom', 0) s
                INNER JOIN
                (SELECT ObjectName, ORDINAL_POSITION FROM flw.AllColumns) a
                    ON s.[ObjectName] = a.[ObjectName]
            ORDER BY a.ORDINAL_POSITION;


            SELECT '##PreIngestionTransfromValues' AS DataSetName,
                   [FlowID],
                   [FlowType],
                   [Virtual],
                   [ColName] AS [SourceColName],
                   '`' + [SelectExp] + '`' AS [TransFormExp],
                   [ColAlias],
                   [ExcludeColFromView]
            FROM flw.PreIngestionTransfrom
            WHERE FlowID = @FlowID;
        END;



        IF OBJECT_ID('tempdb..#PipelineTargetObject') IS NOT NULL
        BEGIN
            DROP TABLE #PipelineTargetObject;
        END;

        SELECT lo.ObjectName AS ObjectName,
               [ObjectType],
               Step,
               ISNULL('`' + lo.ObjectDef + '`', '') AS SchemaCreatedBySQLFlow
        INTO #PipelineTargetObject
        FROM [flw].[LineageMap] lm
            INNER JOIN [flw].[LineageObjectMK] lo
                ON lm.ToObjectMK = lo.ObjectMK
        WHERE FlowID = @FlowID;

        SELECT '#PipelineTargetObject' AS DataSetName,
               ObjectName,
               MAX([ObjectType]) AS [ObjectType],
               MAX(Step) AS Step,
               MAX(SchemaCreatedBySQLFlow) AS SchemaCreatedBySQLFlow
        FROM #PipelineTargetObject
        WHERE ObjectName NOT IN
              (
                  SELECT ObjectName FROM #PipelineSourceObject
              )
        GROUP BY ObjectName
        ORDER BY Step;

        IF (LEN(@InsertCmd) > 0)
        BEGIN
            SELECT '##PipeLineInsertNewRows' AS DataSetName,
                   @InsertCmd InsertCmd;
        END;

        IF (LEN(@UpdateCmd) > 0)
        BEGIN
            SELECT '##PipeLineUpdateExsistingRows' AS DataSetName,
                   @UpdateCmd UpdateCmd;
        END;

        IF (LEN(@DeleteCmd) > 0)
        BEGIN
            SELECT '##PipeLineDeleteRows' AS DataSetName,
                   @DeleteCmd DeleteCmd;
        END;


    END;

END;
GO
