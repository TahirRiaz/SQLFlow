SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[Reset]
  -- Date				:   2023-03-29
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure resets logged ingestion date for imported files. Enables reprocessing of the source folder.
  -- Summary			:	The procedure takes three parameters:

							@Flowid: the ID of the flow to reset.
							@NoOfOverlapDays: the number of days to keep data overlap for. If set to 0, then no overlap is kept.
							@ResetTransformations: a flag indicating whether to reset transformations or not. If set to 1, then transformations are reset.
							The procedure updates the NextExportDate and NextExportValue columns of the flw.SysLog table for the given flow, setting them to 1900-01-01 and 0, respectively.

							If @NoOfOverlapDays is set to a value greater than 0, then the procedure finds the minimum FileDate_DW from the 
							flw.SysLogFile table where FileRowDate_DW is within the given number of days from today's date, and updates the FileDate column of the flw.SysLog table 
							for the given flow to that value. If @NoOfOverlapDays is set to 0, then the FileDate column is simply set to 0.

							If @ResetTransformations is set to 1, then the procedure sets the SelectExp and ColAlias columns of the flw.PreIngestionTransfrom table to NULL for the given flow.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-03-29		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[Reset]
    --Reset file date to number of overlaping days. defualt is 0
    @Flowid INT,
    @NoOfOverlapDays INT = 0,
    @ResetTransformations INT = 0,
	@DeleteTransformations INT = 0
AS
BEGIN
    
	UPDATE trg
	SET trg.NextExportDate = CAST('1900-01-01' AS DATE),
		trg.NextExportValue = 0
	FROM  [flw].[SysLog] trg
	WHERE FlowId = @flowID
	AND FlowType = 'exp'



	



	IF (@NoOfOverlapDays != 0)
    BEGIN
        DECLARE @filedate VARCHAR(255);

        SELECT @filedate = MIN(FileDate_DW)
          FROM [flw].[SysLogFile]
         WHERE Flowid         = @Flowid
           AND FileRowDate_DW >= DATEDIFF(dd, @NoOfOverlapDays * -1, FileRowDate_DW);
        UPDATE Trg
           SET Trg.[FileDate] = ISNULL(@filedate, 0),
				trg.FileDateHist = Trg.[FileDate]
          FROM [flw].[SysLog] Trg
         WHERE FlowId = @Flowid;
    END;
    ELSE
    BEGIN
        UPDATE Trg
           SET Trg.[FileDate] = 0,
			   trg.FileDateHist = Trg.[FileDate],
			   trg.WhereIncExp = NULL,
			   trg.WhereDateExp = NULL,
			   trg.WhereXML = NULL
          FROM [flw].[SysLog] Trg
         WHERE FlowId = @Flowid;

    END;


    IF (@ResetTransformations = 1)
    BEGIN
        UPDATE trg
           SET trg.[SelectExp] = NULL,
               trg.[ColAlias] = NULL
          FROM [flw].[PreIngestionTransfrom] trg
         WHERE FlowId = @Flowid;
    END;

	 IF (@DeleteTransformations = 1)
    BEGIN
        DELETE trg
          FROM [flw].[PreIngestionTransfrom] trg
         WHERE FlowId = @Flowid;
    END;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure resets logged ingestion date for imported files. Enables reprocessing of the source folder.
  -- Summary			:	The procedure takes three parameters:

							@Flowid: the ID of the flow to reset.
							@NoOfOverlapDays: the number of days to keep data overlap for. If set to 0, then no overlap is kept.
							@ResetTransformations: a flag indicating whether to reset transformations or not. If set to 1, then transformations are reset.
							The procedure updates the NextExportDate and NextExportValue columns of the flw.SysLog table for the given flow, setting them to 1900-01-01 and 0, respectively.

							If @NoOfOverlapDays is set to a value greater than 0, then the procedure finds the minimum FileDate_DW from the 
							flw.SysLogFile table where FileRowDate_DW is within the given number of days from today''s date, and updates the FileDate column of the flw.SysLog table 
							for the given flow to that value. If @NoOfOverlapDays is set to 0, then the FileDate column is simply set to 0.

							If @ResetTransformations is set to 1, then the procedure sets the SelectExp and ColAlias columns of the flw.PreIngestionTransfrom table to NULL for the given flow.', 'SCHEMA', N'flw', 'PROCEDURE', N'Reset', NULL, NULL
GO
