SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[SysLogAssertion]
  -- Date				:   2024.01.14
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024.01.14		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddSysLogAssertion]
    -- Add the parameters for the stored procedure here
    @FlowID INT,
    @AssertionID INT,
    @AssertionSqlCmd NVARCHAR(MAX) = '',
    @Result NVARCHAR(255),
	@AssertedValue NVARCHAR(255),
    @TraceLog NVARCHAR(MAX) = ''
AS
BEGIN

    SET NOCOUNT ON;


    IF NOT EXISTS
    (
        SELECT [FlowID],
               [AssertionID]
        FROM [flw].[SysLogAssertion] WITH (NOLOCK)
        WHERE FlowID = @FlowID
              AND [AssertionID] = @AssertionID
    )
    BEGIN
        --Move current log version to Stats
        INSERT INTO flw.SysLogAssertion
        (
            [FlowID],
            [AssertionID],
            [AssertionDate],
            [AssertionSqlCmd],
            [Result],
			AssertedValue,
            [TraceLog]
        )
        SELECT TOP (1)
               @FlowID,
               @AssertionID,
               GETDATE(),
               @AssertionSqlCmd,
               @Result,
			   @AssertedValue,
               @TraceLog;
    END;
    ELSE
    BEGIN
        --Log Exec Start Time
        UPDATE TOP (1)
            trg
        SET trg.AssertionDate = GETDATE(),
            trg.AssertionSqlCmd = @AssertionSqlCmd,
            trg.Result = @Result,
			trg.AssertedValue = @AssertedValue,
            trg.TraceLog = @TraceLog
        FROM [flw].[SysLogAssertion] trg WITH (NOLOCK)
        WHERE FlowID = @FlowID
              AND [AssertionID] = @AssertionID;

    END;



END;
GO
