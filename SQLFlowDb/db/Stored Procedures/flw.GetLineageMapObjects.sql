SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- Stored Procedure

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetLineageMapObjects]
  -- Date				:   2023.09.01
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure will generate DS for Data Lineage Calculation
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023.09.01		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[GetLineageMapObjects]
AS
BEGIN

    DECLARE @CommandTimeout [INT] = 36000,
            @maxConcurrency [INT] = 8;
    --NB! This query is case sensitive due to bulk load 
    SELECT [RecID],
           0 AS [Virtual],
           flwds.batch AS Batch,
           flwds.SysAlias,
           src.[FlowID],
           src.[FlowType],
           ISNULL(src.[FromObjectMK],0) AS [FromObjectMK],
           ISNULL(src.[ToObjectMK],0) AS [ToObjectMK],
           [flw].[RemBrackets]([FromObject]) AS [FromObject],
           [flw].[RemBrackets]([ToObject]) AS [ToObject],
           CAST(NULL AS NVARCHAR(MAX)) AS [PathStr],
           CAST(NULL AS NVARCHAR(MAX)) AS [PathNum],
           NULL AS [RootObjectMK],
           CAST(NULL AS NVARCHAR(255)) AS [RootObject],
           CAST(NULL AS BIT) AS [Circular],
           NULL AS [Step],
           NULL AS [Sequence],
           NULL AS [Level],
           NULL AS [Priority],
           NULL AS [NoOfChildren],
           NULL AS [MaxLevel],

           (
               SELECT SourceIsAzCont FROM [flw].[GetFlowTypeTBL](src.FlowID)
           ) SourceIsAzCont,
           @CommandTimeout AS CommandTimeout,
           @maxConcurrency AS MaxConcurrency,
           ISNULL(sl.[EndTime], '1900-01-01') AS LastExec,
           CAST(ISNULL([Success], -1) AS INT) AS [Status],
           CASE
               WHEN src.DataSet IN ( 200, 100, 2 ) THEN
                   0
               ELSE
                   1
           END AS SolidEdge,
           CASE
               WHEN
               (
                   SELECT TypeIsFile FROM flw.IsSrcTypeFile(flwds.FlowType)
               ) = 1 THEN
               (
                   SELECT TOP 1
                          FileName_DW
                   FROM [flw].[SysLogFile] lg
                   WHERE lg.FlowID = src.FlowID
                   ORDER BY [FileRowDate_DW] DESC
               )
               ELSE
                   NULL
           END AS LatestFileProcessed,
           ISNULL(flwds.DeactivateFromBatch, 0) AS DeactivateFromBatch,
           CAST(1 AS BIT) AS DataStatus,
		   CAST(NULL AS NVARCHAR(MAX)) AS NextStepFlows,
		   sl.SrcAlias,
		   sl.TrgAlias
    FROM [flw].[LineageEdge] src
        LEFT OUTER JOIN [flw].[SysLog] sl
            ON src.FlowID = sl.FlowID
        LEFT OUTER JOIN [flw].[LineageObjectMK] f
            ON src.FromObjectMK = f.ObjectMK
        LEFT OUTER JOIN [flw].[LineageObjectMK] t
            ON src.ToObjectMK = t.ObjectMK
        LEFT OUTER JOIN [flw].[FlowDS] flwds
            ON flwds.FlowID = src.FlowID;




END;
GO
