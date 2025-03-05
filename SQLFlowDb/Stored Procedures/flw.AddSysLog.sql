SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- Stored Procedure

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddSysLog]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to update the logging and statistics information for a specific flow in the flw.SysLog and flw.SysStats tables. 
							It takes several input parameters related to the flow, execution details, error messages, and other information.
  -- Summary			:	The stored procedure performs the following actions:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.

							It moves the current log version to the flw.SysStats table by inserting a new record with the flow information and statistics.

							It updates the start time, execution mode, and other related information in the flw.SysLog table for the current flow.

							It updates various fields in the flw.SysLog table based on the provided input parameters, such as process, flow type, start time, end time, duration, fetched, inserted, updated, deleted, success, no of threads, command strings, error messages, file name, file size, file date, and other related fields.

							If the @BatchID parameter is not '0', it updates the status, start time, and end time fields in the flw.SysLogBatch table for the current flow and batch ID.

							This stored procedure is primarily used for maintaining logging and statistics information for a specific flow in an ETL or data processing system.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddSysLog]
    -- Add the parameters for the stored procedure here
    @FlowID INT,
    @FlowType NVARCHAR(25),
    @ExecMode NVARCHAR(255),
    @Process NVARCHAR(2000) = '',
    @StartTime DATETIME = '',
    @EndTime DATETIME = '',
    @DurationFlow INT = 0,
    @DurationPre INT = 0,
    @DurationPost INT = 0,
    @Fetched INT = 0,
    @Inserted INT = 0,
    @Updated INT = 0,
    @Deleted INT = 0,
    @NoOfThreads INT = 0,
    @SelectCmd NVARCHAR(MAX) = '',
    @InsertCmd NVARCHAR(MAX) = '',
    @UpdateCmd NVARCHAR(MAX) = '',
    @DeleteCmd NVARCHAR(MAX) = '',
    @RuntimeCmd NVARCHAR(MAX) = '',
    @CreateCmd NVARCHAR(MAX) = '',
    @ErrorInsert NVARCHAR(MAX) = '',
    @ErrorUpdate NVARCHAR(MAX) = '',
    @ErrorDelete NVARCHAR(MAX) = '',
    @ErrorRuntime NVARCHAR(MAX) = '',
    @FileName NVARCHAR(MAX) = '',
    @FileSize INT = 0,
    @FileDate VARCHAR(15) = '',
    @SysAlias [NVARCHAR](250) = N'',
    @Batch NVARCHAR(255) = N'',
    @BatchID NVARCHAR(70) = '',
    @WhereIncExp NVARCHAR(MAX) = '',
    @WhereDateExp NVARCHAR(MAX) = '',
    @WhereXML NVARCHAR(MAX) = '',
    @DataTypeWarning NVARCHAR(MAX) = '',
    @ColumnWarning NVARCHAR(MAX) = '',
    @SurrogateKeyCmd NVARCHAR(MAX) = '',
    @NextExportDate NVARCHAR(255) = NULL,
    @NextExportValue NVARCHAR(255) = NULL,
    @dbg INT = 0,
    @TraceLog NVARCHAR(MAX) = '',
	@InferDatatypeCmd NVARCHAR(MAX) = ''

AS
BEGIN

	--cmd.Parameters.Add("@InferDatatypeCmd", SqlDbType.VarChar).Value = logStack.ToString();

    SET NOCOUNT ON;

    --TODO: Ensure that table trigger works 
    --IF NOT EXISTS (   SELECT FlowID
    --                    FROM [flw].[IngestionLog] WITH (NOLOCK)
    --                   WHERE FlowID = @FlowID)
    --BEGIN
    --    INSERT INTO [flw].[IngestionLog] (FlowID)
    --    VALUES (@FlowID);
    --END;


    --Move current log version to Stats
    INSERT INTO flw.SysStats
    (
        [FlowType],
        [StatsDate],
        [FlowID],
        [StartTime],
        [EndTime],
        [DurationFlow],
        [DurationPre],
        [DurationPost],
        [Fetched],
        [Inserted],
        [Updated],
        [Deleted],
        [Success],
        [FlowRate],
        [NoOfThreads],
        [ExecMode],
        [FileName],
        [FileSize],
        [FileDate]
    )
    SELECT TOP (1)
           [FlowType],
           GETDATE() AS [StatsDate],
           [FlowID],
           [StartTime],
           [EndTime],
           [DurationFlow],
           [DurationPre],
           [DurationPost],
           [Fetched],
           [Inserted],
           [Updated],
           [Deleted],
           [Success],
           [FlowRate],
           [NoOfThreads],
           [ExecMode],
           [FileName],
           [FileSize],
           [FileDate]
    FROM flw.SysLog
    WHERE FlowID = @FlowID;

    --Log Exec Start Time
    UPDATE TOP (1)
        trg
    SET trg.[StartTime] = GETDATE(),
        trg.ExecMode = @ExecMode,
        trg.[DurationFlow] = NULL,
        trg.[DurationPre] = NULL,
        trg.[DurationPost] = NULL,
        trg.[Fetched] = NULL,
        trg.[Inserted] = NULL,
        trg.[Updated] = NULL,
        trg.[Deleted] = NULL,
        trg.[Success] = NULL,
        trg.FlowRate = NULL,
        trg.NoOfThreads = NULL,
        trg.[SelectCmd] = NULL,
        trg.[InsertCmd] = NULL,
        trg.[UpdateCmd] = NULL,
        trg.[DeleteCmd] = NULL,
        trg.[RuntimeCmd] = NULL,
        trg.[ErrorInsert] = NULL,
        trg.[ErrorUpdate] = NULL,
        trg.[ErrorDelete] = NULL,
        trg.[ErrorRuntime] = NULL,
        trg.[DataTypeWarning] = NULL,
        trg.[ColumnWarning] = NULL,
        trg.SurrogateKeyCmd = NULL,
        trg.Batch = @Batch,
        trg.SysAlias = @SysAlias
    --trg.[Filename] = NULL,
    --trg.[FileSize] = NULL,
    --trg.FileDate = NULL
    FROM flw.SysLog trg WITH (NOLOCK)
    WHERE trg.FlowID = @FlowID;
    --AND         trg.[FlowType]     = @FlowType;

    DECLARE @curObjName NVARCHAR(255),
            @curExecCmd NVARCHAR(4000),
            @curSection NVARCHAR(4000),
            @curCode NVARCHAR(MAX);

    UPDATE TOP (1)
        trg
    SET [Process] = CASE
                        WHEN LEN(@Process) > 0 THEN
                            @Process
                        ELSE
                            [Process]
                    END,
        [FlowType] = CASE
                         WHEN LEN(@FlowType) > 0 THEN
                             @FlowType
                         ELSE
                             [FlowType]
                     END,
        [StartTime] = CASE
                          WHEN LEN(NULLIF(@StartTime, '1900-01-01')) > 0 THEN
                              @StartTime
                          ELSE
                              ISNULL(NULLIF(StartTime, '1900-01-01'), GETDATE())
                      END,
        [EndTime] = CASE
                        WHEN LEN(NULLIF(@EndTime, '1900-01-01')) > 0 THEN
                            @EndTime
                        ELSE
                            GETDATE()
                    END,
        DurationFlow = CASE
                           WHEN LEN(@DurationFlow) >= 0 THEN
                               @DurationFlow
                           ELSE
                               DurationFlow
                       END,
        DurationPre = CASE
                          WHEN LEN(@DurationPre) >= 0 THEN
                              @DurationPre
                          ELSE
                              DurationPre
                      END,
        DurationPost = CASE
                           WHEN LEN(@DurationPost) >= 0 THEN
                               @DurationPost
                           ELSE
                               DurationPost
                       END,
        Fetched = CASE
                      WHEN Fetched = 0
                           OR Fetched IS NULL THEN
                          @Fetched
                      ELSE
                          Fetched
                  END,
        [Inserted] = CASE
                         WHEN Inserted = 0
                              OR Inserted IS NULL THEN
                             @Inserted
                         ELSE
                             Inserted
                     END,
        [Updated] = CASE
                        WHEN Updated = 0
                             OR Updated IS NULL THEN
                            @Updated
                        ELSE
                            Updated
                    END,
        [Deleted] = CASE
                        WHEN Deleted = 0
                             OR Deleted IS NULL THEN
                            @Deleted
                        ELSE
                            Deleted
                    END,
        [Success] = CASE
                        WHEN LEN(@ErrorInsert) > 0
                             OR LEN(@ErrorUpdate) > 0
                             OR LEN(@ErrorDelete) > 0
                             OR LEN(@ErrorRuntime) > 0 THEN
                            0
                        ELSE
                            1
                    END,
        NoOfThreads = CASE
                          WHEN LEN(@NoOfThreads) > 0 THEN
                              @NoOfThreads
                          ELSE
                              NoOfThreads
                      END,
        [FlowRate] = CASE
                         WHEN @DurationFlow > 0 THEN
                             IIF(@DurationFlow > 0, @Fetched / @DurationFlow, 0)
                         ELSE
                             [FlowRate]
                     END,
        [SelectCmd] = CASE
                          WHEN LEN(@SelectCmd) > 5 THEN
                              @SelectCmd
                          ELSE
                              SelectCmd
                      END,
        [InsertCmd] = CASE
                          WHEN LEN(@InsertCmd) > 5 THEN
                              @InsertCmd
                          ELSE
                              InsertCmd
                      END,
        [UpdateCmd] = CASE
                          WHEN LEN(@UpdateCmd) > 5 THEN
                              @UpdateCmd
                          ELSE
                              UpdateCmd
                      END,
        [DeleteCmd] = CASE
                          WHEN LEN(@DeleteCmd) > 5 THEN
                              @DeleteCmd
                          ELSE
                              DeleteCmd
                      END,
        [RuntimeCmd] = CASE
                           WHEN LEN(@RuntimeCmd) > 0 THEN
                               @RuntimeCmd
                           ELSE
                               RuntimeCmd
                       END,
        [CreateCmd] = CASE
                          WHEN LEN(@CreateCmd) > 5 THEN
                              @CreateCmd
                          ELSE
                              CreateCmd
                      END,
        ErrorInsert = CASE
                          WHEN LEN(@ErrorInsert) > 0 THEN
                              @ErrorInsert
                          ELSE
                              ErrorInsert
                      END,
        ErrorUpdate = CASE
                          WHEN LEN(@ErrorUpdate) > 0 THEN
                              @ErrorUpdate
                          ELSE
                              ErrorUpdate
                      END,
        ErrorDelete = CASE
                          WHEN LEN(@ErrorDelete) > 0 THEN
                              @ErrorDelete
                          ELSE
                              ErrorDelete
                      END,
        ErrorRuntime = CASE
                           WHEN LEN(@ErrorRuntime) > 0 THEN
                               @ErrorRuntime
                           ELSE
                               ErrorRuntime
                       END,
        [FileName] = CASE
                         WHEN LEN(@FileName) >= 0 THEN
                             @FileName
                         ELSE
                             [FileName]
                     END,
        FileSize = CASE
                       WHEN FileSize = 0
                            OR FileSize IS NULL THEN
                           @FileSize
                       ELSE
                           FileSize
                   END,
        FileDate = CASE
                       WHEN LEN(@FileDate) > 0 THEN
                           @FileDate
                       ELSE
                           [FileDate]
                   END,
        WhereIncExp = CASE
                          WHEN LEN(@WhereIncExp) > 2 THEN
                              @WhereIncExp
                          ELSE
                              WhereIncExp
                      END,
        WhereDateExp = CASE
                           WHEN LEN(@WhereDateExp) > 2 THEN
                               @WhereDateExp
                           ELSE
                               WhereDateExp
                       END,
        WhereXML = CASE
                       WHEN LEN(@WhereXML) > 2 THEN
                           @WhereXML
                       ELSE
                           WhereXML
                   END,
        DataTypeWarning = CASE
                              WHEN LEN(@DataTypeWarning) > 0 THEN
                                  @DataTypeWarning
                              ELSE
                                  [DataTypeWarning]
                          END,
        ColumnWarning = CASE
                            WHEN LEN(@ColumnWarning) > 0 THEN
                                @ColumnWarning
                            ELSE
                                [ColumnWarning]
                        END,
        SurrogateKeyCmd = CASE
                              WHEN LEN(@SurrogateKeyCmd) > 2 THEN
                                  @SurrogateKeyCmd
                              ELSE
                                  SurrogateKeyCmd
                          END,
        NextExportDate = CASE
                             WHEN LEN(@NextExportDate) > 0 THEN
                                 @NextExportDate
                             ELSE
                                 NextExportDate
                         END,
        NextExportValue = CASE
                              WHEN LEN(@NextExportValue) > 0 THEN
                                  @NextExportValue
                              ELSE
                                  NextExportValue
                          END,
        TraceLog = CASE
                       WHEN LEN(@TraceLog) > 0 THEN
                           @TraceLog
                       ELSE
                           TraceLog
                   END,

		InferDatatypeCmd = CASE
                       WHEN LEN(@InferDatatypeCmd) > 0 THEN
                           @InferDatatypeCmd
                       ELSE
                           InferDatatypeCmd
                   END

    FROM flw.SysLog trg
    WHERE FlowID = @FlowID;

    IF (@BatchID <> '0')
    BEGIN
        UPDATE TOP (1)
            trg
        SET [Status] = CASE
                           WHEN LEN(@ErrorInsert) > 0
                                OR LEN(@ErrorUpdate) > 0
                                OR LEN(@ErrorDelete) > 0
                                OR LEN(@ErrorRuntime) > 0 THEN
                               'Error'
                           ELSE
                               CASE
                                   WHEN LEN(NULLIF(@EndTime, '1900-01-01')) > 0 THEN
                                       'Done'
                                   ELSE
                                       'Running'
                               END
                       END,
            [StartTime] = CASE
                              WHEN LEN(NULLIF(@StartTime, '1900-01-01')) > 0 THEN
                                  @StartTime
                              ELSE
                                  GETDATE()
                          END,
            [EndTime] = CASE
                            WHEN LEN(@ErrorInsert) > 10
                                 OR LEN(@ErrorUpdate) > 10
                                 OR LEN(@ErrorDelete) > 10
                                 OR LEN(@ErrorRuntime) > 10 THEN
                                CAST(NULL AS DATETIME)
                            ELSE
                                CASE
                                    WHEN LEN(NULLIF(@EndTime, '1900-01-01')) > 0 THEN
                                        @EndTime
                                    ELSE
                                        GETDATE()
                                END
                        END
        FROM flw.SysLogBatch trg
        WHERE FlowID = @FlowID
              AND BatchID = @BatchID;

    END;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to update the logging and statistics information for a specific flow in the flw.SysLog and flw.SysStats tables. 
							It takes several input parameters related to the flow, execution details, error messages, and other information.
  -- Summary			:	The stored procedure performs the following actions:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.

							It moves the current log version to the flw.SysStats table by inserting a new record with the flow information and statistics.

							It updates the start time, execution mode, and other related information in the flw.SysLog table for the current flow.

							It updates various fields in the flw.SysLog table based on the provided input parameters, such as process, flow type, start time, end time, duration, fetched, inserted, updated, deleted, success, no of threads, command strings, error messages, file name, file size, file date, and other related fields.

							If the @BatchID parameter is not ''0'', it updates the status, start time, and end time fields in the flw.SysLogBatch table for the current flow and batch ID.

							This stored procedure is primarily used for maintaining logging and statistics information for a specific flow in an ETL or data processing system.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddSysLog', NULL, NULL
GO
