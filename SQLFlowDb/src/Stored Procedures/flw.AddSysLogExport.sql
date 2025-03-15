SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddSysLogExport]
  -- Date				:   2023-03-24
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures purpose is to log export-related information in the flw.SysLogExport table. It takes several input parameters related to the export operation, 
							such as BatchID, FlowID, SQL command, where clause, file path, file name, file size, file rows, next export date, and next export value.
  -- Summary			:	The stored procedure performs the following actions:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.

							It inserts a new record into the flw.SysLogExport table with the provided input parameters and the current date as the export date.

							This stored procedure is primarily used for logging export operations and their related information in an ETL or data processing system. 
							By keeping a record of these operations, it is possible to monitor and audit the export processes and troubleshoot any issues that may arise.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-03-24		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[AddSysLogExport]
    -- Add the parameters for the stored procedure here
    @BatchID NVARCHAR(50),
    @FlowID INT,
    @SqlCMD VARCHAR(MAX),
    @WhereClause NVARCHAR(1024),
    @FilePath_DW NVARCHAR(255),
    @FileName_DW NVARCHAR(255),
    @FileSize_DW DECIMAL(18, 0),
    @FileRows_DW INT,
    @NextExportDate DATETIME = NULL,
    @NextExportValue INT = NULL
AS
BEGIN

    SET NOCOUNT ON;

    INSERT INTO [flw].[SysLogExport]
    (
        [BatchID],
        [FlowID],
        [SqlCMD],
        [WhereClause],
        [FilePath_DW],
        [FileName_DW],
        [FileSize_DW],
        [FileRows_DW],
        [NextExportDate],
        [NextExportValue],
        [ExportDate]
    )
    SELECT @BatchID,
           @FlowID,
           @SqlCMD,
           @WhereClause,
           @FilePath_DW,
           @FileName_DW,
           @FileSize_DW,
           @FileRows_DW,
           @NextExportDate,
           @NextExportValue,
           GETDATE();
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures purpose is to log export-related information in the flw.SysLogExport table. It takes several input parameters related to the export operation, 
							such as BatchID, FlowID, SQL command, where clause, file path, file name, file size, file rows, next export date, and next export value.
  -- Summary			:	The stored procedure performs the following actions:

							It sets NOCOUNT to ON, which suppresses the display of the count of the number of rows affected by the operations within the stored procedure.

							It inserts a new record into the flw.SysLogExport table with the provided input parameters and the current date as the export date.

							This stored procedure is primarily used for logging export operations and their related information in an ETL or data processing system. 
							By keeping a record of these operations, it is possible to monitor and audit the export processes and troubleshoot any issues that may arise.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddSysLogExport', NULL, NULL
GO
