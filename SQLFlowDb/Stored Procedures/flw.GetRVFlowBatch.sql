SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetRVFlowBatch]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to build a metadata dataset for Batch Executions
  -- Summary			:	It accepts several input parameters such as @BatchList, @FlowType, @SysAlias, and @dbg, and it performs various operations to manage the execution of batches.

							A high-level summary of what the stored procedure does:

							It sets up some initial variable values, including @curObjName, @curSection, @dbName, @FlowType, and @NoOfThreads.

							It determines the appropriate number of threads to use based on the system configuration and the given batch list.

							It sets up some more variables, such as @commandTimeout, @maxConcurrency, @BatchID, @IncompletBatchID, and @BatchStatus.

							It checks if any previous batch is still running. If not, it inserts new records into the flw.SysLogBatch table with the current batch information.

							If a previous batch is running, it retrieves the unfinished batch's details.

							Finally, it returns the batch details to be executed, ordered by the Step value.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetRVFlowBatch]
    @BatchList VARCHAR(255) = 'AW19', -- Batch name from [flw].[Ingestion]
    @FlowType VARCHAR(255) = '',      -- ING Ingestion, PRE Preingestion, SHE Shellscript
    @SysAlias VARCHAR(255) = '',      -- System Alias from [flw].[Ingestion]
    @dbg INT = 0
AS
BEGIN

    DECLARE @curObjName NVARCHAR(255),
            @curSection NVARCHAR(4000),
            @dbName NVARCHAR(255) = DB_NAME();

    SET @FlowType = LTRIM(RTRIM(@FlowType));
    --SET @curObjName = ISNULL(OBJECT_SCHEMA_NAME(@@PROCID) + '.' + OBJECT_NAME(@@PROCID), 'Defualt SP Name');
    --SET @curExecCmd = N'exec ' + @curObjName + N' @Batch =''' + @Batch + N''';';
    --PRINT [flw].[GetLogHeader](@curObjName, @curExecCmd);
    --truncate table [flw].[SysBatch]
    --@Batch varchar(255) = 'VLSmall',
    DECLARE @BatchCMD VARCHAR(MAX) = '',
            @logg VARCHAR(255);


    DECLARE @NoOfThreads INT = 0;

    SELECT @NoOfThreads = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = N'NoOfThreads');

    SELECT @NoOfThreads = CASE
                              WHEN @NoOfThreads > MIN(NoOfThreads) THEN
                                  MIN(NoOfThreads)
                              ELSE
                                  @NoOfThreads
                          END
    FROM flw.SysBatch
    WHERE Batch IN
          (
              SELECT Item FROM [flw].[StringSplit](@BatchList, ',')
          )
    GROUP BY Batch;


    DECLARE @commandTimeout [INT] = 36000,
            @maxConcurrency [INT] = @NoOfThreads;
    --@dbg            [INT]           = 1;

    DECLARE @BatchID VARCHAR(70),
            @IncompletBatchID VARCHAR(70),
            @BatchStatus INT = 0;
    SET @BatchID = FORMAT(GETDATE(), 'yyyyMMddHHmmssfff');

    --Check if privous batch is done?
    SELECT @BatchStatus = COUNT(*),
           @IncompletBatchID = MAX(b.[BatchID])
    FROM flw.SysLogBatch b
        INNER JOIN [flw].[SysBatchSteps] bs
            ON b.FlowID = bs.FlowID
    WHERE b.[EndTime] IS NULL
          AND bs.[OnErrorResume] = 0
          AND b.Batch IN
              (
                  SELECT Item FROM [flw].[StringSplit](@BatchList, ',')
              );


    --SET @BatchStatus  = 0;
    --If batch is not running
    IF (@BatchStatus = 0)
    BEGIN
        INSERT INTO flw.SysLogBatch
        (
            [BatchID],
            [Batch],
            [FlowID],
            [FlowType],
            [SysAlias],
            [Step],
            [Sequence],
            dbg,
            [Status],
            [BatchTime],
            SourceIsAzCont
        )
        SELECT @BatchID AS BatchID,
               Batch,
               FlowID,
               FlowType,
               SysAlias,
               Step,
               [Sequence],
               @dbg,
               'Queue' AS [Status],
               GETDATE() AS BatchTime,
               (
                   SELECT SourceIsAzCont FROM [flw].[GetFlowTypeTBL](FlowID)
               ) SourceIsAzCont
        FROM [flw].[SysBatchSteps] sbs
        WHERE ISNULL(DeactivateFromBatch, 0) = 0
              AND Batch IN
                  (
                      SELECT Item FROM [flw].[StringSplit](@BatchList, ',')
                  )
              AND FlowType = CASE
                                 WHEN LEN(LTRIM(RTRIM(@FlowType))) > 0 THEN
                                     LTRIM(RTRIM(@FlowType))
                                 ELSE
                                     FlowType
                             END
              AND SysAlias = CASE
                                 WHEN LEN(@SysAlias) > 0 THEN
                                     @SysAlias
                                 ELSE
                                     SysAlias
                             END;


        SELECT b.BatchID,
               b.Batch,
               b.FlowID,
               LOWER(b.FlowType) AS FlowType,
               b.SysAlias,
               b.Step,
               b.Sequence,
               bs.OnErrorResume,
               ISNULL(b.dbg, 0) AS dbg,
               b.[Status],
               @commandTimeout AS commandTimeout,
               @maxConcurrency AS maxConcurrency,
               SourceIsAzCont
        FROM flw.SysLogBatch AS b
            INNER JOIN flw.SysBatchSteps AS bs
                ON b.FlowID = bs.FlowID
        WHERE BatchID = @BatchID
        ORDER BY b.Step;
    END;
    ELSE
    BEGIN

        --get unfinished batch
        SELECT b.BatchID,
               b.Batch,
               b.FlowID,
               LOWER(b.FlowType) FlowType,
               b.SysAlias,
               b.Step,
               b.Sequence,
               bs.OnErrorResume,
               ISNULL(b.dbg, 0) AS dbg,
               b.[Status],
               @commandTimeout AS commandTimeout,
               @maxConcurrency AS maxConcurrency,
               SourceIsAzCont
        FROM flw.SysLogBatch AS b
            INNER JOIN flw.SysBatchSteps AS bs
                ON b.FlowID = bs.FlowID
        WHERE ISNULL(bs.DeactivateFromBatch, 0) = 0
              AND BatchID = @IncompletBatchID
              AND b.FlowType = CASE
                                   WHEN LEN(@FlowType) > 0 THEN
                                       @FlowType
                                   ELSE
                                       b.FlowType
                               END
              AND b.SysAlias = CASE
                                   WHEN LEN(@SysAlias) > 0 THEN
                                       @SysAlias
                                   ELSE
                                       b.SysAlias
                               END
              AND b.EndTime IS NULL
        ORDER BY b.Step;
    --AND Batch IN ( SELECT Item FROM [flw].[StringSplit](@BatchList, ',') );

    END;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to build a metadata dataset for Batch Executions
  -- Summary			:	It accepts several input parameters such as @BatchList, @FlowType, @SysAlias, and @dbg, and it performs various operations to manage the execution of batches.

							A high-level summary of what the stored procedure does:

							It sets up some initial variable values, including @curObjName, @curSection, @dbName, @FlowType, and @NoOfThreads.

							It determines the appropriate number of threads to use based on the system configuration and the given batch list.

							It sets up some more variables, such as @commandTimeout, @maxConcurrency, @BatchID, @IncompletBatchID, and @BatchStatus.

							It checks if any previous batch is still running. If not, it inserts new records into the flw.SysLogBatch table with the current batch information.

							If a previous batch is running, it retrieves the unfinished batch''s details.

							Finally, it returns the batch details to be executed, ordered by the Step value.', 'SCHEMA', N'flw', 'PROCEDURE', N'GetRVFlowBatch', NULL, NULL
GO
