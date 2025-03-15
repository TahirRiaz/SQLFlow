SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO







CREATE VIEW [flw].[PreFiles]
AS
WITH baseVal
AS (
   SELECT [flw].[GetCFGParamVal]('DefaultColDataType') AS pDefaultColDataType)
SELECT FlowID,
       FlowType,
       CASE
           WHEN LEN(DefaultColDataType) > 0 THEN
               DefaultColDataType
           ELSE
               pDefaultColDataType
       END AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionCSV] csv
    CROSS APPLY baseVal a
UNION ALL
SELECT FlowID,
       FlowType,
       CASE
           WHEN LEN(DefaultColDataType) > 0 THEN
               DefaultColDataType
           ELSE
               pDefaultColDataType
       END AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionXML] [XML]
    CROSS APPLY baseVal a
UNION ALL
SELECT FlowID,
       FlowType,
       CASE
           WHEN LEN(DefaultColDataType) > 0 THEN
               DefaultColDataType
           ELSE
               pDefaultColDataType
       END AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionXLS]
    CROSS APPLY baseVal a
UNION ALL
SELECT FlowID,
       FlowType,
       pDefaultColDataType AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionPRQ]
    CROSS APPLY baseVal a
UNION ALL
SELECT FlowID,
       FlowType,
       pDefaultColDataType AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionPRC]
    CROSS APPLY baseVal a
UNION ALL
SELECT FlowID,
       FlowType,
       CASE
           WHEN LEN(DefaultColDataType) > 0 THEN
               DefaultColDataType
           ELSE
               pDefaultColDataType
       END AS DefaultColDataType,
       trgDBSchTbl,
       ISNULL(preFilter, '') AS preFilter
FROM [flw].[PreIngestionJSN] [JSN]
    CROSS APPLY baseVal a
GO
