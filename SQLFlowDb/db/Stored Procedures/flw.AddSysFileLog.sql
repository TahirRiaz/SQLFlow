SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddSysFileLog]
  -- Date				:   2020.11.08
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to insert a new record into the flw.SysLogFile table.
  -- Summary			:	It takes the following input parameters:

							@BatchID (NVARCHAR(50)): The batch identifier.
							@FlowID (INT): The flow identifier.
							@FileDate_DW (VARCHAR(255)): The file date.
							@FileName_DW (NVARCHAR(255)): The file name.
							@FileRowDate_DW (DATETIME): The file row date.
							@FileSize_DW (DECIMAL(18,0)): The file size.
							@FileColumnCount (INT, default NULL): The number of columns in the file.
							@ExpectedColumnCount (INT, default NULL): The expected number of columns.
							Here's a summary of the stored procedure:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.
							It inserts a new record into the flw.SysLogFile table with the provided values for the input parameters.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2020.11.08		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[AddSysFileLog]
    -- Add the parameters for the stored procedure here
    @BatchID  NVARCHAR(50),
	@FlowID INT,
    @DataSet_DW VARCHAR(255),
    @FileDate_DW VARCHAR(255),
	@FileName_DW NVARCHAR(255),
    @FileRowDate_DW DATETIME,
	@FileSize_DW DECIMAL(18,0),
	@FileColumnCount INT = NULL,
	@ExpectedColumnCount INT = NULL
AS
BEGIN

    SET NOCOUNT ON;

    INSERT INTO flw.SysLogFile ([BatchID], [FlowID], [FileDate_DW], DataSet_DW, [FileName_DW], [FileRowDate_DW], [FileSize_DW], FileColumnCount, ExpectedColumnCount)
    SELECT @BatchID, @FlowID, @FileDate_DW, @DataSet_DW, @FileName_DW, @FileRowDate_DW, @FileSize_DW, @FileColumnCount, @ExpectedColumnCount
      
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to insert a new record into the flw.SysLogFile table.
  -- Summary			:	It takes the following input parameters:

							@BatchID (NVARCHAR(50)): The batch identifier.
							@FlowID (INT): The flow identifier.
							@FileDate_DW (VARCHAR(255)): The file date.
							@FileName_DW (NVARCHAR(255)): The file name.
							@FileRowDate_DW (DATETIME): The file row date.
							@FileSize_DW (DECIMAL(18,0)): The file size.
							@FileColumnCount (INT, default NULL): The number of columns in the file.
							@ExpectedColumnCount (INT, default NULL): The expected number of columns.
							Here''s a summary of the stored procedure:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.
							It inserts a new record into the flw.SysLogFile table with the provided values for the input parameters.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddSysFileLog', NULL, NULL
GO
