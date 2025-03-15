SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[IsKeyColumn]
  -- Date				:   2022-12-05
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[IsKeyColumn] is to determine if a specified column in a given flow is a key column. 
  -- Summary			:	The function takes two input parameters: @FlowID and @ColName.

							The function first removes any square brackets from the @ColName parameter and then fetches the key column list from the [flw].[Ingestion] 
							table for the given flow ID. The list of key columns is a comma-separated string of column names.

							The function checks if the @ColName exists in the list of key columns. If the @ColName is found in the list, 
							the function returns 1 indicating that the column is a key column. Otherwise, the function returns 0 indicating that the column is not a key column. 
							If the returned value is null, the function sets it to 0 before returning it.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-12-05		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[IsKeyColumn] (@FlowID INT, @ColName NVARCHAR(255))
RETURNS BIT
BEGIN

    DECLARE @rValue BIT = 0;

	SET @ColName =  [flw].[RemBrackets](@ColName)
	DECLARE @KeyColumns NVARCHAR(1024) = '';

	SELECT @KeyColumns = [KeyColumns] FROM [flw].[Ingestion] WHERE [FlowID] = @FlowID
	
	SELECT @rValue = COUNT(*)
	FROM [flw].[StringSplit](@KeyColumns,',')
	WHERE [flw].[RemBrackets](item) = @ColName

	IF(@rValue IS NULL)
		SET @rValue = 0
	
    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[IsKeyColumn] is to determine if a specified column in a given flow is a key column. 
  -- Summary			:	The function takes two input parameters: @FlowID and @ColName.

							The function first removes any square brackets from the @ColName parameter and then fetches the key column list from the [flw].[Ingestion] 
							table for the given flow ID. The list of key columns is a comma-separated string of column names.

							The function checks if the @ColName exists in the list of key columns. If the @ColName is found in the list, 
							the function returns 1 indicating that the column is a key column. Otherwise, the function returns 0 indicating that the column is not a key column. 
							If the returned value is null, the function sets it to 0 before returning it.', 'SCHEMA', N'flw', 'FUNCTION', N'IsKeyColumn', NULL, NULL
GO
