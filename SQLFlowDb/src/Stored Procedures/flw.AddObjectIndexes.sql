SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO





/*
  ##################################################################################################################################################
  -- Name				:   [flw].[AddObjectIndexes]
  -- Date				:   2023-11-03
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedure is responsible for updating the target indexes of an object in the [flw].[SysLog] table.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@TrgIndexes (default value: '')
							It starts by setting the NOCOUNT option to ON to prevent the sending of row count messages to the client.

							It updates the TrgIndexes field for the record with the specified FlowID in the [flw].[SysLog] table. 
							If the @TrgIndexes parameter has a length greater than 0, the field will be set to the provided value; 
							otherwise, it retains its existing value.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-11-03		Initial
  ##################################################################################################################################################
  */

CREATE PROCEDURE [flw].[AddObjectIndexes]
    -- Add the parameters for the stored procedure here
    @FlowID INT,
	@TrgIndexes NVARCHAR(MAX) = ''
AS
BEGIN

    SET NOCOUNT ON;
    
    UPDATE TOP (1) trg
       SET         TrgIndexes = CASE
                                   WHEN LEN(@TrgIndexes) > 0 THEN @TrgIndexes
                                   ELSE TrgIndexes END

      FROM         flw.SysLog trg
     WHERE         FlowID = @FlowID;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedure is responsible for updating the target indexes of an object in the [flw].[SysLog] table.
  -- Summary			:	It accepts the following input parameters:

							@FlowID
							@TrgIndexes (default value: '''')
							It starts by setting the NOCOUNT option to ON to prevent the sending of row count messages to the client.

							It updates the TrgIndexes field for the record with the specified FlowID in the [flw].[SysLog] table. 
							If the @TrgIndexes parameter has a length greater than 0, the field will be set to the provided value; 
							otherwise, it retains its existing value.', 'SCHEMA', N'flw', 'PROCEDURE', N'AddObjectIndexes', NULL, NULL
GO
