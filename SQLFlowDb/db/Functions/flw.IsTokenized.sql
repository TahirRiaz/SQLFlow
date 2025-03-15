SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[IsTokenized]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[IsTokenized] is to check if a given flow has tokenization enabled.
  -- Summary			:	The function takes an input parameter @FlowID, which is the identifier of the flow being checked. 
							The function queries the [flw].[IngestionTokenize] table to count the number of rows with the specified @FlowID. 
							If the count is greater than zero, the function returns 1 indicating that tokenization is enabled for the specified flow. 
							Otherwise, the function returns 0 indicating that tokenization is not enabled for the specified flow. 
							If the returned value is null, the function sets it to 0 before returning it.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[IsTokenized]
(
    @FlowID INT
)
RETURNS BIT
BEGIN

    DECLARE @rValue BIT;

    SELECT @rValue = COUNT(*)
    FROM flw.IngestionTokenize
    WHERE (FlowID = @FlowID);

    IF (@rValue IS NULL)
        SET @rValue = 0;

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[IsTokenized] is to check if a given flow has tokenization enabled.
  -- Summary			:	The function takes an input parameter @FlowID, which is the identifier of the flow being checked. 
							The function queries the [flw].[IngestionTokenize] table to count the number of rows with the specified @FlowID. 
							If the count is greater than zero, the function returns 1 indicating that tokenization is enabled for the specified flow. 
							Otherwise, the function returns 0 indicating that tokenization is not enabled for the specified flow. 
							If the returned value is null, the function sets it to 0 before returning it.', 'SCHEMA', N'flw', 'FUNCTION', N'IsTokenized', NULL, NULL
GO
