SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[AddSysLogMatchKey]
    @MatchKeyID INT = 0,
    @FlowID INT = 0,
    @BatchID INT = NULL,
    @SysAlias NVARCHAR(70) = NULL,
    @Batch NVARCHAR(250) = NULL,
    @StartTime DATETIME = NULL,
    @EndTime DATETIME = NULL,
    @Status BIT = NULL,
    @DurationMatch INT = NULL,
    @DurationPre INT = NULL,
    @DurationPost INT = NULL,
    @SrcRowCount INT = NULL,
    @SrcDelRowCount INT = NULL,
    @TrgRowCount INT = NULL,
    @TrgDelRowCount INT = NULL,
    @TaggedRowCount INT = NULL,
    @ErrorMessage NVARCHAR(MAX) = NULL,
    @TraceLog NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    
    INSERT INTO [flw].[SysLogMatchKey] (
        [MatchKeyID],
        [FlowID],
        [BatchID],
        [SysAlias],
        [Batch],
        [StartTime],
        [EndTime],
        [Status],
        [DurationMatch],
        [DurationPre],
        [DurationPost],
        [SrcRowCount],
        [SrcDelRowCount],
        [TrgRowCount],
        [TrgDelRowCount],
        [TaggedRowCount],
        [ErrorMessage],
        [TraceLog]
    )
    VALUES (
        @MatchKeyID,
        @FlowID,
        @BatchID,
        @SysAlias,
        @Batch,
        @StartTime,
        @EndTime,
        CASE WHEN LEN(@ErrorMessage) > 0 THEN 0 ELSE 1 END,
        @DurationMatch,
        @DurationPre,
        @DurationPost,
        @SrcRowCount,
        @SrcDelRowCount,
        @TrgRowCount,
        @TrgDelRowCount,
        @TaggedRowCount,
        @ErrorMessage,
        @TraceLog
    );
END;
GO
