SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddSysFileLog]
  -- Date				:   2024.05.22
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024.05.22		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[AddSysFileEventLog]
    -- Add the parameters for the stored procedure here
	@FlowID INT,
	@FileName_DW NVARCHAR(255)
AS
BEGIN

    SET NOCOUNT ON;

    INSERT INTO [flw].[SysLogFileEvent] ([FlowID], [FileName_DW], [EventDate_DW])
    SELECT @FlowID,  @FileName_DW, GETDATE()
    
END;
GO
