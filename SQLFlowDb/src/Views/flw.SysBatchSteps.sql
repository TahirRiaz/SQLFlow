SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




















CREATE VIEW [flw].[SysBatchSteps]
AS
WITH base
AS (
   SELECT FlowID,
          MAX(p.Step) Step
   FROM [flw].[LineageMap] AS p
   --WHERE (p.Virtual = 0)
   --AND FlowID = 1033
   GROUP BY FlowID)
SELECT DISTINCT
       COALESCE(sh.Batch, sp.Batch, xls.Batch, [xml].Batch, csv.Batch, I.Batch, ado.Batch, [exp].Batch, jsn.Batch,  prq.Batch , prc.Batch) AS Batch,
       p.FlowID,
       LOWER(COALESCE(
                         CASE
                             WHEN sh.InvokeType IS NOT NULL THEN
                                 'inv'
                         END,
                         sp.FlowType,
                         xls.FlowType,
                         [xml].FlowType,
                         csv.FlowType,
                         I.FlowType,
                         ado.FlowType,
                         [exp].FlowType,
                         jsn.FlowType,
                         prq.FlowType,
                         prc.FlowType
                     )
            ) AS FlowType,
       COALESCE(
                   sh.SysAlias,
                   sp.SysAlias,
                   xls.SysAlias,
                   [xml].SysAlias,
                   csv.SysAlias,
                   I.SysAlias,
                   ado.SysAlias,
                   [exp].SysAlias,
                   jsn.SysAlias,
                   prq.SysAlias,
                   prc.SysAlias
               ) AS SysAlias,
       COALESCE(
                   sh.DeactivateFromBatch,
                   sp.DeactivateFromBatch,
                   xls.DeactivateFromBatch,
                   [xml].DeactivateFromBatch,
                   csv.DeactivateFromBatch,
                   I.DeactivateFromBatch,
                   ado.DeactivateFromBatch,
                   [exp].DeactivateFromBatch,
                   jsn.DeactivateFromBatch,
                   prq.DeactivateFromBatch,
                   prc.DeactivateFromBatch
               ) AS DeactivateFromBatch,
       ISNULL(
                 COALESCE(
                             sh.OnErrorResume,
                             sp.OnErrorResume,
                             xls.OnErrorResume,
                             [xml].OnErrorResume,
                             csv.OnErrorResume,
                             I.OnErrorResume,
                             ado.OnErrorResume,
                             [exp].OnErrorResume,
                             jsn.OnErrorResume,
                             prq.OnErrorResume,
                             prc.OnErrorResume
                         ),
                 1
             ) AS OnErrorResume,
       CASE
           WHEN LOWER(COALESCE(
                                  CASE
                                      WHEN sh.InvokeType IS NOT NULL THEN
                                          'inv'
                                  END,
                                  sp.FlowType,
                                  xls.FlowType,
                                  [xml].FlowType,
                                  csv.FlowType,
                                  I.FlowType,
                                  ado.FlowType,
                                  [exp].FlowType,
                                  jsn.FlowType,
                                  prq.FlowType,
                                  prc.FlowType
                              )
                     ) = 'vew' THEN
               p.Step - 100
           ELSE
               p.Step
       END AS Step, --View should have the same step as its source table
       1 AS [Sequence],
       GETDATE() AS BatchTime,
       GETDATE() AS [StartTime],
       GETDATE() AS [EndTime]
FROM base AS p
    LEFT OUTER JOIN flw.Invoke AS sh
        ON p.FlowID = sh.FlowID
    LEFT OUTER JOIN flw.StoredProcedure AS sp
        ON p.FlowID = sp.FlowID
    LEFT OUTER JOIN flw.PreIngestionXLS AS xls
        ON p.FlowID = xls.FlowID
    LEFT OUTER JOIN flw.PreIngestionXML AS [xml]
        ON p.FlowID = [xml].FlowID
    LEFT OUTER JOIN flw.PreIngestionCSV AS csv
        ON p.FlowID = csv.FlowID
    LEFT OUTER JOIN flw.PreIngestionADO AS ado
        ON p.FlowID = ado.FlowID
    LEFT OUTER JOIN flw.Ingestion AS I
        ON p.FlowID = I.FlowID
    LEFT OUTER JOIN [flw].[Export] AS [exp]
        ON p.FlowID = [exp].FlowID
    LEFT OUTER JOIN flw.PreIngestionJSN AS [jsn]
        ON p.FlowID = [jsn].FlowID
    LEFT OUTER JOIN flw.PreIngestionPRQ AS [prq]
        ON p.FlowID = [prq].FlowID
    LEFT OUTER JOIN flw.PreIngestionPRC AS [prc]
        ON p.FlowID = [prc].FlowID;
GO
