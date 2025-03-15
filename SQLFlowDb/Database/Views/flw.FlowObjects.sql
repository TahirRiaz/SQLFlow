SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO











CREATE VIEW [flw].[FlowObjects]
AS
WITH EXPSrc
AS (
   SELECT DISTINCT
          SysAlias,
          [srcFile] AS [ObjectName],
          'prc' AS [ObjectType],
          'prcfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionPRC]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'prctrg' AS [ObjectSource]
   FROM [flw].[PreIngestionPRC]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [trgFileName] AS [ObjectName],
          'exp' AS [ObjectType],
          'expfiles' AS [ObjectSource]
   FROM [flw].[Export]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(srcDBSchTbl) AS srcDBSchTbl,
          'tbl' AS [ObjectType],
          'expsrc' AS [ObjectSource]
   FROM [flw].[Export]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [srcFile],
          'prq' AS [ObjectType],
          'prqfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionPRQ]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'prqtrg' AS [ObjectSource]
   FROM [flw].[PreIngestionPRQ]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          InvokeAlias AS InvokeAlias,
          InvokeType AS InvokeType,
          'inv' AS [ObjectSource]
   FROM flw.Invoke
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [flw].[GetADOSourceName](FlowID) [srcObject],
          'ado' AS [ObjectType],
          'ADOObject' AS [ObjectSource]
   FROM [flw].[PreIngestionADO]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'ADOtrg' AS [ObjectSource]
   FROM [flw].[PreIngestionADO]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [srcFile],
          'csv' AS [ObjectType],
          'csvfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionCSV]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'csvtrg' AS [ObjectSource]
   FROM [flw].[PreIngestionCSV]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [srcFile],
          'xml' AS [ObjectType],
          'xmlfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionXML]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'xmltrg' AS [ObjectSource]
   FROM [flw].[PreIngestionXML]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [srcFile],
          'jsn' AS [ObjectType],
          'jsnfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionJSN]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'jsntrg' AS [ObjectSource]
   FROM [flw].[PreIngestionJSN]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          [srcFile],
          'xls' AS [ObjectType],
          'xlsfiles' AS [ObjectSource]
   FROM [flw].[PreIngestionXLS]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'xlstrg' AS [ObjectSource]
   FROM [flw].[PreIngestionXLS]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName(srcDBSchTbl) AS srcDBSchTbl,
          'source' AS [ObjectType],
          'ingsrc' AS [ObjectSource]
   FROM [flw].[Ingestion]
   UNION ALL
   SELECT DISTINCT
          '' SysAlias, --Avoid Duplicate Values For Target Objects
          flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'ingtrg' AS [ObjectSource]
   FROM [flw].[Ingestion]
   UNION ALL
   SELECT DISTINCT
          '' SysAlias, --Avoid Duplicate Values For Target Objects
          flw.GetValidSrcTrgName([SurrogateDbSchTbl]) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'ingSKey' AS [ObjectSource]
   FROM [flw].[Ingestion] i
       INNER JOIN [flw].[SurrogateKey] s
           ON i.[FlowID] = s.[FlowID]
   UNION ALL
   SELECT DISTINCT
          '' SysAlias, --Avoid Duplicate Values For Target Objects
          [flw].[GetMKeyTrgName](i.[trgDBSchTbl], s.[MatchKeyID]) AS trgDBSchTbl,
          'tbl' AS [ObjectType],
          'ingMKey' AS [ObjectSource]
   FROM [flw].[Ingestion] i
       INNER JOIN [flw].[MatchKey] s
           ON i.[FlowID] = s.[FlowID]
   UNION ALL
   SELECT DISTINCT
          SysAlias,
          flw.GetValidSrcTrgName([trgDBSchSP]) AS [trgDBSchSP],
          'sp' AS [ObjectType],
          'sp' AS [ObjectSource]
   FROM flw.StoredProcedure)
SELECT *
FROM EXPSrc;
GO
