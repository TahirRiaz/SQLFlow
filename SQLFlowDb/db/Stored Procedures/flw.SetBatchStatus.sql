SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[SetBatchStatus]
  -- Date				:   2023-03-09
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this stored procedure is to update the "DeactivateFromBatch" column in various tables within the "[flw]" schema based on the provided batch name and status.
							The default value for status is 1 if not specified.
  -- Summary			:	The stored procedure achieves this by performing several update statements on tables named "Ingestion", "PreIngestionADO", 
							"PreIngestionCSV", "PreIngestionXLS", "PreIngestionXML", "Invoke", "Export" and "PrePostSP". Each update statement updates the "DeactivateFromBatch" column in the respective table 
							where the "Batch" column value is equal to the provided batch name and sets it to the provided status value.

							Overall, this stored procedure provides a convenient way to update the status of various tables in the database schema based on a given batch name.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-03-09		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[SetBatchStatus]
(
    -- Add the parameters for the stored procedure here
    @Batch NVARCHAR(250),
    @Status INT = 1
)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON;

    -- Insert statements for procedure here
    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[Ingestion] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionADO] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionCSV] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionXLS] trg
    WHERE Batch = @Batch;


    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionXML] trg
    WHERE Batch = @Batch;


	UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionPRQ] trg
    WHERE Batch = @Batch;

	UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionPRC] trg
    WHERE Batch = @Batch;

	UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[PreIngestionJSN] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[Invoke] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM [flw].[Export] trg
    WHERE Batch = @Batch;

    UPDATE trg
    SET trg.DeactivateFromBatch = @Status
    FROM flw.StoredProcedure trg
    WHERE Batch = @Batch;


END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this stored procedure is to update the "DeactivateFromBatch" column in various tables within the "[flw]" schema based on the provided batch name and status.
							The default value for status is 1 if not specified.
  -- Summary			:	The stored procedure achieves this by performing several update statements on tables named "Ingestion", "PreIngestionADO", 
							"PreIngestionCSV", "PreIngestionXLS", "PreIngestionXML", "Invoke", "Export" and "PrePostSP". Each update statement updates the "DeactivateFromBatch" column in the respective table 
							where the "Batch" column value is equal to the provided batch name and sets it to the provided status value.

							Overall, this stored procedure provides a convenient way to update the status of various tables in the database schema based on a given batch name.', 'SCHEMA', N'flw', 'PROCEDURE', N'SetBatchStatus', NULL, NULL
GO
