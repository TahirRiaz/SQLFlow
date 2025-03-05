SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[BatchStatus]
  -- Date				:   2022-12-07
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure, named flw.BatchStatus, retrieves the status of a specific batch by providing
							the batch name as an input parameter. It is designed to help monitor and audit the progress 
							of various ETL or data processing steps within a given batch. 
  -- Summary			:	The stored procedure performs the following actions:

							It takes a single input parameter @Batch, which represents the batch name.

							It selects the relevant columns from the flw.SysLogBatch table and joins it with the flw.SysLog table on the FlowID column.

							It filters the results by considering the latest batch by using the MAX function on the BatchID column and the provided batch name as a filter.

							It orders the results by the Step column, which represents the order of each step within the batch.

							By using this stored procedure, users can quickly obtain information about the current status of a specific batch, such as which steps have 
							been completed and which are still in progress. This information can be useful for monitoring the overall progress of data processing 
							operations and identifying any bottlenecks or issues that may arise during execution.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-12-07		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[BatchStatus] @Batch NVARCHAR(50) = ''
AS
BEGIN
    --DECLARE @Batch nvarchar(50) = 'fara-dat'
    SELECT b.*,
           l.[Process]
    FROM [flw].[SysLogBatch] b
        INNER JOIN [flw].[SysLog] l
            ON b.FlowID = l.FlowID
    WHERE [BatchID] in
    (
        SELECT MAX([BatchID])
        FROM [flw].[SysLogBatch]
        WHERE Batch = CASE WHEN LEN(@Batch) = 0 THEN Batch ELSE @Batch END
		AND b.BatchTime > DATEADD(d,-14,GETDATE())
        GROUP BY [Batch]
    )
	AND [Status] = CASE WHEN LEN(@Batch) = 0 THEN 'Queue' ELSE [Status] END 
	
    ORDER BY [Batch], Step
END;

GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure, named flw.BatchStatus, retrieves the status of a specific batch by providing
							the batch name as an input parameter. It is designed to help monitor and audit the progress 
							of various ETL or data processing steps within a given batch. 
  -- Summary			:	The stored procedure performs the following actions:

							It takes a single input parameter @Batch, which represents the batch name.

							It selects the relevant columns from the flw.SysLogBatch table and joins it with the flw.SysLog table on the FlowID column.

							It filters the results by considering the latest batch by using the MAX function on the BatchID column and the provided batch name as a filter.

							It orders the results by the Step column, which represents the order of each step within the batch.

							By using this stored procedure, users can quickly obtain information about the current status of a specific batch, such as which steps have 
							been completed and which are still in progress. This information can be useful for monitoring the overall progress of data processing 
							operations and identifying any bottlenecks or issues that may arise during execution.', 'SCHEMA', N'flw', 'PROCEDURE', N'BatchStatus', NULL, NULL
GO
