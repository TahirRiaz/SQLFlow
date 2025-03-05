SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[LineageEdgeBase]
AS
SELECT 1 AS DataSet,
       i.FlowID,
       'ing' AS FlowType,
       i.FromObjectMK,
       i.ToObjectMK,
       i.srcDBSchTbl AS FromObject,
       flw.GetValidSrcTrgName(i.trgDBSchTbl) AS ToObject,
       ISNULL(F.[BeforeDependency], t.[BeforeDependency]) AS Dependency,
       0 AS IsAfterDependency
FROM flw.Ingestion i
    INNER JOIN flw.LineageObjectMK F
        ON i.FromObjectMK = F.ObjectMK
    INNER JOIN flw.LineageObjectMK t
        ON i.ToObjectMK = t.ObjectMK
UNION
--Skey Objects
SELECT 2 AS DataSet,
       i.FlowID,
       'ing' AS FlowType,
       sk.[ToObjectMK],
       i.ToObjectMK,
       sk.[SurrogateDbSchTbl],
       flw.GetValidSrcTrgName(i.trgDBSchTbl),
       NULL [BeforeDependency],
       0 AS IsInsertDepSP
FROM flw.Ingestion i
    INNER JOIN [flw].[SurrogateKey] sk
        ON i.FlowID = sk.FlowID
    INNER JOIN flw.LineageObjectMK t
        ON i.ToObjectMK = t.ObjectMK
    INNER JOIN flw.LineageObjectMK k
        ON sk.[ToObjectMK] = k.ObjectMK
UNION
--Dependency of FromObject
SELECT 3 AS DataSet,
       flw.Ingestion.FlowID,
       'ing' AS FlowType,
       [flw].[GetObjectMK](Item, flw.Ingestion.SysAlias) AS FromObjectMK,
       flw.Ingestion.[FromObjectMK] AS ToObjectMK,
       Item AS FromObject,
       flw.Ingestion.srcDBSchTbl AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM flw.Ingestion
    INNER JOIN flw.LineageObjectMK
        ON flw.Ingestion.FromObjectMK = flw.LineageObjectMK.ObjectMK
    CROSS APPLY
(SELECT Item FROM [flw].[StringSplit]([BeforeDependency], ',') ) a
UNION
--Dependency of ToObject
SELECT 4 AS DataSet,
       flw.Ingestion.FlowID,
       'ing' AS FlowType,
       [flw].[GetObjectMK](Item, flw.Ingestion.SysAlias) AS FromObjectMK,
       flw.Ingestion.[ToObjectMK] AS ToObjectMK,
       Item AS FromObject,
       flw.Ingestion.trgDBSchTbl AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM flw.Ingestion
    INNER JOIN flw.LineageObjectMK
        ON flw.Ingestion.ToObjectMK = flw.LineageObjectMK.ObjectMK
    CROSS APPLY
(SELECT Item FROM [flw].[StringSplit]([BeforeDependency], ',') ) a
UNION

--#[flw].[Procedure]
--Regular 
SELECT 5 AS DataSet,
       i.FlowID,
       '' AS FlowType,
       i.FromObjectMK,
       i.ToObjectMK,
       NULL AS FromObject,
       [trgDBSchSP] AS ToObject,
       t.[BeforeDependency],
       0 AS IsInsertDepSP
FROM flw.StoredProcedure i
    INNER JOIN flw.LineageObjectMK t
        ON i.ToObjectMK = t.ObjectMK
WHERE (
          LEN(ISNULL(t.[BeforeDependency], '')) = 0
          AND LEN(ISNULL(t.AfterDependency, '')) = 0
      )
UNION
--Dependency of ToObject


SELECT 6 AS DataSet,
       i.FlowID,
       'sp' AS FlowType,
       ISNULL([flw].[GetObjectMK](Item, i.SysAlias), i.[ToObjectMK]) AS FromObjectMK,
       ISNULL(i.[ToObjectMK], [flw].[GetObjectMK](Item, i.SysAlias)) AS ToObjectMK,
       ISNULL(Item, i.[trgDBSchSP]) AS FromObject,
       ISNULL(i.[trgDBSchSP], Item) AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM flw.StoredProcedure i
    INNER JOIN flw.LineageObjectMK
        ON i.ToObjectMK = flw.LineageObjectMK.ObjectMK
    CROSS APPLY
(SELECT Item FROM [flw].[StringSplit]([BeforeDependency], ',') ) a
UNION
--[InsertDepSP] of ToObject
SELECT 7 AS DataSet,
       i.FlowID,
       'sp' AS FlowType,
       i.[ToObjectMK] AS FromObjectMK,
       [flw].[GetObjectMK](Item, i.SysAlias) AS ToObjectMK,
       i.[trgDBSchSP] AS FromObject,
       Item AS ToObject,
       NULL AS Dependency,
       1 AS IsInsertDepSP
FROM flw.StoredProcedure i
    INNER JOIN flw.LineageObjectMK
        ON i.ToObjectMK = flw.LineageObjectMK.ObjectMK
    CROSS APPLY
(SELECT Item FROM [flw].[StringSplit]([AfterDependency], ',') ) a
UNION
--#[flw].[PreIngestionADO]
--Regular FromObject and ToObject
SELECT 8 AS DataSet,
       ADO.FlowID FlowID,
       'ado' AS FlowType,
       ADO.FromObjectMK,
       ADO.ToObjectMK,
       [flw].[GetADOSourceName](ADO.FlowID),
       flw.GetValidSrcTrgName(ADO.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionADO] ADO
    INNER JOIN flw.LineageObjectMK
        ON ADO.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionADO] PreShellScriptList of ToObject
SELECT 9 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       [flw].[GetADOSourceName](i.FlowID) AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionADO] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[Export]
--Regular FromObject and ToObject
SELECT 10 AS DataSet,
       [exp].FlowID FlowID,
       'exp' AS FlowType,
       [exp].FromObjectMK,
       [exp].ToObjectMK,
       flw.GetValidSrcTrgName([exp].srcDBSchTbl),
       [exp].[trgFileName],
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[Export] [exp]
    INNER JOIN flw.LineageObjectMK
        ON [exp].FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--#[flw].[PreIngestionCSV]
--Regular FromObject and ToObject
SELECT 11 AS DataSet,
       csv.FlowID FlowID,
       'csv' AS FlowType,
       csv.FromObjectMK,
       csv.ToObjectMK,
       csv.[srcFile],
       flw.GetValidSrcTrgName(csv.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionCSV] csv
    INNER JOIN flw.LineageObjectMK
        ON csv.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionCSV] PreShellScriptList of ToObject
SELECT 12 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionCSV] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[PreIngestionxls]
--Regular FromObject and ToObject
SELECT 13 AS DataSet,
       xls.FlowID FlowID,
       'xls' AS FlowType,
       xls.FromObjectMK,
       xls.ToObjectMK,
       xls.[srcFile],
       flw.GetValidSrcTrgName(xls.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionXLS] xls
    INNER JOIN flw.LineageObjectMK
        ON xls.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionxls] PreShellScriptList of ToObject
SELECT 14 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionXLS] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[PreIngestionxml]
--Regular FromObject and ToObject
SELECT 15 AS DataSet,
       xml.FlowID FlowID,
       'xml' AS FlowType,
       xml.FromObjectMK,
       xml.ToObjectMK,
       xml.[srcFile],
       flw.GetValidSrcTrgName(xml.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionXML] xml
    INNER JOIN flw.LineageObjectMK
        ON xml.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionxml] PreShellScriptList of ToObject
SELECT 16 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionXML] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--[flw].[Ingestion] of FromObject
SELECT 17 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcDBSchTbl] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[Ingestion] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--[flw].[Ingestion] of ToObject
SELECT 18 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       i.[ToObjectMK] AS FromObjectMK,
       [flw].[GetObjectMK](PostInvokeAlias, i.SysAlias) AS ToObjectMK,
       i.[trgDBSchTbl] AS FromObject,
       PostInvokeAlias AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[Ingestion] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PostInvokeAlias = ss.InvokeAlias
UNION
--[flw].[PrePostSP] of ToObject
SELECT 19 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       i.[ToObjectMK] AS FromObjectMK,
       [flw].[GetObjectMK](PostInvokeAlias, i.SysAlias) AS ToObjectMK,
       i.[trgDBSchTbl] AS FromObject,
       PostInvokeAlias AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[Ingestion] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PostInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[PreIngestionprq]
--Regular FromObject and ToObject
SELECT 20 AS DataSet,
       prq.FlowID FlowID,
       'prq' AS FlowType,
       prq.FromObjectMK,
       prq.ToObjectMK,
       prq.[srcFile],
       flw.GetValidSrcTrgName(prq.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionPRQ] prq
    INNER JOIN flw.LineageObjectMK
        ON prq.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionprq] PreShellScriptList of ToObject
SELECT 21 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionPRQ] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[PreIngestionJSN]
--Regular FromObject and ToObject
SELECT 20 AS DataSet,
       JSN.FlowID FlowID,
       'jsn' AS FlowType,
       JSN.FromObjectMK,
       JSN.ToObjectMK,
       JSN.[srcFile],
       flw.GetValidSrcTrgName(JSN.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionJSN] JSN
    INNER JOIN flw.LineageObjectMK
        ON JSN.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionJSN] PreShellScriptList of ToObject
SELECT 21 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionJSN] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
--#[flw].[PreIngestionprc]
--Regular FromObject and ToObject
SELECT 22 AS DataSet,
       prc.FlowID FlowID,
       'prc' AS FlowType,
       prc.FromObjectMK,
       prc.ToObjectMK,
       prc.[srcFile],
       flw.GetValidSrcTrgName(prc.trgDBSchTbl),
       NULL Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionPRC] prc
    INNER JOIN flw.LineageObjectMK
        ON prc.FromObjectMK = flw.LineageObjectMK.ObjectMK
UNION
--[PreIngestionprc] PreShellScriptList of ToObject
SELECT 23 AS DataSet,
       ss.FlowID,
       'inv' AS FlowType,
       [flw].[GetObjectMK](PreInvokeAlias, i.SysAlias) AS FromObjectMK,
       i.[FromObjectMK] AS ToObjectMK,
       PreInvokeAlias AS FromObject,
       i.[srcFile] AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[PreIngestionPRC] i
    INNER JOIN flw.LineageObjectMK
        ON i.FromObjectMK = flw.LineageObjectMK.ObjectMK
    INNER JOIN flw.Invoke ss
        ON i.PreInvokeAlias = ss.InvokeAlias
UNION
SELECT 24 AS DataSet,
       i.FlowID,
       'sub' AS FlowType,
       ISNULL([flw].[GetObjectMK](Item, ''), i.[ToObjectMK]) AS FromObjectMK,
       ISNULL(i.[ToObjectMK], [flw].[GetObjectMK](Item, '')) AS ToObjectMK,
       ISNULL(Item, i.SubscriberName) AS FromObject,
       ISNULL(i.SubscriberName, Item) AS ToObject,
       NULL AS Dependency,
       0 AS IsInsertDepSP
FROM [flw].[DataSubscriber] i
    INNER JOIN flw.LineageObjectMK
        ON i.ToObjectMK = flw.LineageObjectMK.ObjectMK
    CROSS APPLY
(SELECT Item FROM [flw].[StringSplit]([BeforeDependency], ',') ) a

UNION
--Skey Objects
SELECT 25 AS DataSet,
       i.FlowID,
       'ing' AS FlowType,
       sk.[ToObjectMK],
       i.ToObjectMK,
	   [flw].[GetMKeyTrgName](i.[trgDBSchTbl], sk.MatchKeyID),
	   i.[trgDBSchTbl],
       NULL [BeforeDependency],
       0 AS IsInsertDepSP
FROM flw.Ingestion i
    INNER JOIN [flw].[MatchKey] sk
        ON i.FlowID = sk.FlowID
    INNER JOIN flw.LineageObjectMK t
        ON i.ToObjectMK = t.ObjectMK
    INNER JOIN flw.LineageObjectMK k
        ON sk.[ToObjectMK] = k.ObjectMK

GO
