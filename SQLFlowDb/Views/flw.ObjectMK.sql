SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO








CREATE VIEW [flw].[ObjectMK]
AS
SELECT DISTINCT
       ToObjectMK AS ObjectMK,
       SysAlias,
       [trgFileName] +' (' + [trgPath] + ')' AS ObjectName,
       'exp' AS [ObjectType],
       'expTrg' AS [ObjectSource]
FROM [flw].[Export]
UNION ALL
SELECT DISTINCT
       FromObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName(srcDBSchTbl) AS srcDBSchTbl,
       'tbl' AS [ObjectType],
       'expSrc' AS [ObjectSource]
FROM [flw].[Export]
UNION ALL
SELECT DISTINCT
       ToObjectMK AS ObjectMK,
       SysAlias,
       InvokeAlias AS InvokeAlias,
       InvokeType AS InvokeType,
       'inv' AS [ObjectSource]
FROM flw.Invoke
UNION ALL
SELECT DISTINCT
       FromObjectMK AS ObjectMK,
       SysAlias,
       [flw].[GetADOSourceName](FlowID) [srcObject],
       'ado' AS [ObjectType],
       'adoObject' AS [ObjectSource]
FROM [flw].[PreIngestionADO]
UNION ALL
SELECT DISTINCT
       ToObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'adoTrg' AS [ObjectSource]
FROM [flw].[PreIngestionADO]
UNION ALL
SELECT DISTINCT
	   FromObjectMK AS ObjectMK,	
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'csv' AS [ObjectType],
       'csvFiles' AS [ObjectSource]
FROM [flw].[PreIngestionCSV]
UNION ALL
SELECT DISTINCT
	   ToObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'csvTrg' AS [ObjectSource]
FROM [flw].[PreIngestionCSV]
UNION ALL
SELECT DISTINCT
	   FromObjectMK AS ObjectMK,
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'xml' AS [ObjectType],
       'xmlFiles' AS [ObjectSource]
FROM [flw].[PreIngestionXML]
UNION ALL
SELECT DISTINCT
	ToObjectMK AS ObjectMK,	
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'xmlTrg' AS [ObjectSource]
FROM [flw].[PreIngestionXML]
UNION ALL
SELECT DISTINCT
	   FromObjectMK AS ObjectMK,
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'jsn' AS [ObjectType],
       'jsnFiles' AS [ObjectSource]
FROM [flw].[PreIngestionJSN]
UNION ALL
SELECT DISTINCT
		ToObjectMK AS ObjectMK,	
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'jsnTrg' AS [ObjectSource]
FROM [flw].[PreIngestionJSN]
UNION ALL
SELECT DISTINCT
		FromObjectMK AS ObjectMK,
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'prq' AS [ObjectType],
       'prqFiles' AS [ObjectSource]
FROM [flw].[PreIngestionPRQ]
UNION ALL
SELECT DISTINCT
ToObjectMK AS ObjectMK,	
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'prqTrg' AS [ObjectSource]
FROM [flw].[PreIngestionPRQ]
UNION ALL
SELECT DISTINCT
FromObjectMK AS ObjectMK,
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'prc' AS [ObjectType],
       'prcFiles' AS [ObjectSource]
FROM [flw].[PreIngestionPRC]
UNION ALL
SELECT DISTINCT
ToObjectMK AS ObjectMK,	
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'prcTrg' AS [ObjectSource]
FROM [flw].[PreIngestionPRC]
UNION ALL
SELECT DISTINCT
FromObjectMK AS ObjectMK,
       SysAlias,
       [srcFile] +' (' + [srcPath] + ')',
       'xls' AS [ObjectType],
       'xlsFiles' AS [ObjectSource]
FROM [flw].[PreIngestionXLS]
UNION ALL
SELECT DISTINCT
ToObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'xlsTrg' AS [ObjectSource]
FROM [flw].[PreIngestionXLS]
UNION ALL
SELECT DISTINCT
FromObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName(srcDBSchTbl) AS srcDBSchTbl,
       'tbl' AS [ObjectType],
       'ing' AS [ObjectSource]
FROM [flw].[Ingestion]
UNION ALL
SELECT DISTINCT
		ToObjectMK AS ObjectMK,
       '' SysAlias, --Avoid Duplicate Values For Target Objects
       flw.GetValidSrcTrgName(trgDBSchTbl) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'ing' AS [ObjectSource]
FROM [flw].[Ingestion]
UNION ALL
SELECT DISTINCT
	  -1 AS ObjectMK,
       '' SysAlias, --Avoid Duplicate Values For Target Objects
       flw.GetValidSrcTrgName([SurrogateDbSchTbl]) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'ing' AS [ObjectSource]
FROM [flw].[Ingestion] i
    INNER JOIN [flw].[SurrogateKey] s
        ON i.[FlowID] = s.[FlowID]
UNION ALL
SELECT DISTINCT
	  -1 AS ObjectMK,
       '' SysAlias, --Avoid Duplicate Values For Target Objects
       [flw].[GetMKeyTrgName](i.[trgDBSchTbl],s.MatchKeyID) AS trgDBSchTbl,
       'tbl' AS [ObjectType],
       'ing' AS [ObjectSource]
FROM [flw].[Ingestion] i
    INNER JOIN [flw].[MatchKey] s
        ON i.[FlowID] = s.[FlowID]
UNION ALL
SELECT DISTINCT
	    ToObjectMK AS ObjectMK,
       SysAlias,
       flw.GetValidSrcTrgName([trgDBSchSP]) AS [trgDBSchSP],
       'sp' AS [ObjectType],
       'sp' AS [ObjectSource]
FROM flw.StoredProcedure
UNION ALL
SELECT DISTINCT
	    ToObjectMK AS ObjectMK,
       '' SysAlias,
       [SubscriberName] ,
       'sub' AS [ObjectType],
       'sub' AS [ObjectSource]
FROM [flw].[DataSubscriber];;

GO
