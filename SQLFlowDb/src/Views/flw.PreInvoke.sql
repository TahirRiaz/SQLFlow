SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[PreInvoke]
AS
SELECT   FlowID, PreInvokeAlias
FROM            flw.PreIngestionADO
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            flw.PreIngestionCSV
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            flw.PreIngestionJSN
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            [flw].[PreIngestionPRQ]
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            [flw].[PreIngestionPRQ]
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            [flw].[PreIngestionPRC]
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM            [flw].[PreIngestionPRC]
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM           [flw].[PreIngestionXLS]
WHERE LEN(PreInvokeAlias) > 0
UNION ALL
SELECT   FlowID, PreInvokeAlias
FROM           [flw].[PreIngestionXML]
WHERE LEN(PreInvokeAlias) > 0
GO
