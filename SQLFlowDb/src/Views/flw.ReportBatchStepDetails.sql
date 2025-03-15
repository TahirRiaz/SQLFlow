SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[ReportBatchStepDetails]
AS

WITH base
AS (
SELECT MAX([BatchID]) AS [BatchID] ,[FlowID], MAX(EndTime) EndTime
     FROM [flw].[SysLogBatch]
	 WHERE [BatchID] <> '0'
     GROUP BY  [FlowID]
	),
MaxValue 
AS
(
	SELECT base.[FlowID], base.EndTime , src.StartTime
	FROM [flw].[SysLogBatch] src 
		INNER JOIN base ON base.[FlowID] = src.FlowID 
			AND base.[BatchID] = src.[BatchID]
)

SELECT TOP 100 PERCENT
       lm.FlowID,
       lm.Batch,
       MAX(lm.Step) AS Step,
       sl.[ProcessShort],
       ISNULL([Success], 0) AS [Success],
       COALESCE(MIN(ds.StartTime), MIN(sl.StartTime), GETDATE()) AS StartTime,
       COALESCE(MAX(ds.EndTime), MAX(sl.EndTime), GETDATE()) AS EndTime,
       ISNULL(SUM(sl.DurationFlow), 0) AS Duration
FROM flw.SysLog AS sl
    INNER JOIN flw.LineageMap AS lm
        ON sl.FlowID = lm.FlowID
    INNER JOIN MaxValue AS ds
        ON sl.FlowID = ds.FlowID
WHERE sl.StartTime > DATEADD(d, -90, GETDATE())
GROUP BY lm.FlowID,
         lm.Batch,
         sl.[ProcessShort],
         [Success]
ORDER BY lm.Batch;
GO
