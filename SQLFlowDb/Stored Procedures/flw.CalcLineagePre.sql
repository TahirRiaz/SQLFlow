SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[CalcLineagePre]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is executed as pre step to the lineage calculation. 
							Its purpose is to generate Master Key for all SQLflow objects.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[CalcLineagePre] @Alias NVARCHAR(50) = ''
AS
BEGIN

    SET NOCOUNT ON;

    --Tag Invalid Objects
    UPDATE trg
    SET trg.NotInUse = 1
    FROM [flw].[LineageObjectMK] trg
    WHERE ObjectType IN ( 'tbl', 'sp', 'vew' )
          AND ObjectMK NOT IN
              (
                  SELECT ObjectMK FROM [flw].[ObjectDS]
              );

    --Ensure that SysTable is in sync
    EXEC [flw].[SyncSysLog];

    --Cleanup SP Name in all FlowTables
    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[srcDBSchTbl] = flw.GetValidSrcTrgName(trg.[srcDBSchTbl]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[Ingestion] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionCSV] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionXML] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionJSN] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionXLS] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionPRQ] trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionPRC] trg;

    UPDATE trg
    SET trg.trgDBSchSP = [flw].[ProcNameCleanup](trg.trgDBSchSP)
    FROM flw.StoredProcedure trg;

    UPDATE trg
    SET trg.trgDBSchSP = [flw].[ProcNameCleanup](trg.trgDBSchSP)
    FROM flw.StoredProcedure trg;

    UPDATE trg
    SET trg.[PreProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PreProcessOnTrg]),
        trg.[PostProcessOnTrg] = [flw].[ProcNameCleanup](trg.[PostProcessOnTrg]),
        trg.[trgDBSchTbl] = flw.GetValidSrcTrgName(trg.[trgDBSchTbl])
    FROM [flw].[PreIngestionADO] trg;


    UPDATE trg
    SET trg.Batch = src.Batch
    FROM [flw].[SysLogBatch] trg
        INNER JOIN [flw].[FlowDS] src
            ON trg.FlowID = src.FlowID
    WHERE trg.Batch COLLATE Latin1_General_BIN <> src.Batch COLLATE Latin1_General_BIN;

    INSERT INTO flw.LineageObjectMK
    (
        [SysAlias],
        [ObjectName],
        [ObjectType],
        [ObjectSource],
        [IsFlowObject]
    )
    SELECT DISTINCT
           MAX(src.[SysAlias]) AS [SysAlias],
           src.[ObjectName] AS [ObjectName],
           MAX(src.[ObjectType]) AS [ObjectType],
           MAX(src.[ObjectSource]) AS [ObjectSource],
           1
    FROM [flw].[ObjectMK] AS src
        LEFT OUTER JOIN flw.LineageObjectMK trg
            ON src.[ObjectName] = trg.ObjectName
    WHERE (trg.[ObjectName] IS NULL)
    GROUP BY src.[ObjectName];
    --AND src.[ObjectSource] = 'ing'
    --AND src.[ObjectType] IN ('csv','xls','xml','jsn', 'prq')

    --Push Master Key Back To Various Tables
    --Multiple objects can push to same target table. Thats why we join on SysAlias as well
    --EXP
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = ISNULL(trg.ObjectMK, trg2.ObjectMK)
    FROM [flw].[Export] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.SysAlias = trg.SysAlias
               AND I.[trgFileName] + ' (' + [trgPath] + ')' = trg.ObjectName
        LEFT OUTER JOIN flw.LineageObjectMK AS trg2
            ON I.[trgFileName] + ' (' + [trgPath] + ')' = trg2.ObjectName --Join without sysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.srcDBSchTbl = src.ObjectName;



    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK
    FROM flw.Ingestion AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.srcDBSchTbl = src.ObjectName
    --AND I.SysAlias = src.SysAlias
    ;


    UPDATE I
    SET I.[ToObjectMK] = trg.ObjectMK
    FROM flw.Ingestion AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --[flw].[SurrogateKey]
    UPDATE I
    SET I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[SurrogateKey] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.[SurrogateDbSchTbl] = trg.ObjectName;


    --[flw].[MatchKey]
    UPDATE mk
    SET mk.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[MatchKey] AS mk
        INNER JOIN flw.Ingestion ing
            ON mk.FlowID = ing.FlowID
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON [flw].[GetMKeyTrgName](ing.[trgDBSchTbl], mk.MatchKeyID) = trg.ObjectName;

    --[flw].[Procedure] 
    UPDATE I
    SET I.[FromObjectMK] = NULL,
        I.[ToObjectMK] = trg.ObjectMK
    FROM flw.StoredProcedure AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.[trgDBSchSP] = trg.ObjectName;

    --ADO
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionADO] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON [flw].[GetADOSourceName](I.FlowID) = src.ObjectName
        --AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --CSV
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionCSV] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
        -- AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --PRQ
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionPRQ] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
        --AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --PRC
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionPRC] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
        --AND I.SysAlias = src.SysAlias 
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --XLS
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionXLS] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
        -- AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;

    --XML
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionXML] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
        -- AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;


    --JSN
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK,
        I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[PreIngestionJSN] AS I
        LEFT OUTER JOIN flw.LineageObjectMK AS src
            ON I.[srcFile] + ' (' + [srcPath] + ')' = src.ObjectName
               AND I.SysAlias = src.SysAlias
        LEFT OUTER JOIN flw.LineageObjectMK AS trg
            ON I.trgDBSchTbl = trg.ObjectName;


    --SS
    UPDATE I
    SET I.[ToObjectMK] = trg.ObjectMK
    FROM flw.Invoke AS I
        INNER JOIN flw.LineageObjectMK AS trg
            ON I.InvokeAlias = trg.ObjectName;


    --Reusing Target Table
    UPDATE I
    SET I.[FromObjectMK] = src.ObjectMK
    FROM flw.Ingestion AS I
        INNER JOIN flw.LineageObjectMK AS src
            ON I.srcDBSchTbl = src.ObjectName
    WHERE I.[FromObjectMK] IS NULL;

    --Sync IsFlowObject. Post Pre Object moved to [flw].[PrePostSP]
    UPDATE trg
    SET trg.IsFlowObject = 1
    FROM [flw].[LineageObjectMK] trg
        INNER JOIN [flw].[FlowObjects] src
            ON trg.[ObjectName] = src.[ObjectName];


    --Sub
    UPDATE I
    SET I.[ToObjectMK] = trg.ObjectMK
    FROM [flw].[DataSubscriber] AS I
        INNER JOIN flw.LineageObjectMK AS trg
            ON I.[SubscriberName] = trg.ObjectName;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose:   This stored procedure is executed as pre step to the lineage calculation. Its purpose is to generate Master Key for all SQLflow objects.', 'SCHEMA', N'flw', 'PROCEDURE', N'CalcLineagePre', NULL, NULL
GO
