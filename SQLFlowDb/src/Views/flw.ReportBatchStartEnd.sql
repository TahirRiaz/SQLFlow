SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO








CREATE VIEW [flw].[ReportBatchStartEnd]
AS

WITH BASE 
AS 
(
SELECT [BatchID],
           sl.[Batch],
           CASE
               WHEN SUM(   CASE
                               WHEN [Status] = 'Done' THEN
                                   1
                               ELSE
                                   0
                           END
                       ) = COUNT(*) THEN
                   1
               ELSE
                   0
           END [Success],
           MIN(StartTime) AS StartTime,
           MAX(EndTime) AS EndTime,
           DATEDIFF(ms, MIN(StartTime), MAX(EndTime)) Duration
    FROM [flw].[SysLogBatch] sl
        INNER JOIN flw.FlowDS AS ds
            ON sl.FlowID = ds.FlowID
    WHERE StartTime > DATEADD(mm, -6, GETDATE())
    --AND Batch = 'Citybike'
    GROUP BY [BatchID],
             sl.[Batch]
),

AvrageTime
AS (

SELECT [BatchID],
       [Batch],
       [Success],
       StartTime,
       EndTime,
       Duration,
       AVG(Duration) OVER (PARTITION BY [Batch]) Average
FROM BASE
)

SELECT [BatchID],
       [Batch],
       [Success],
       StartTime,
       ISNULL(EndTime,DATEADD(ms,Average,StartTime)) AS EndTime, 
       ISNULL(Duration,Average) AS Duration
FROM AvrageTime
GO
