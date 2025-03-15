SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[RemAddObjFromList]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[RemAddObjFromList] is to remove or add a value to a comma-separated list of strings.
  -- Summary			:	The function takes three input parameters: @List, which is the comma-separated list of strings being modified, @Val, 
							which is the value being removed or added to the list, and @AddRem, which is the operation being performed.

							The function splits the @List parameter into individual items using the [flw].[StringSplit] function and removes any 
							items that match the @Val parameter or are not valid object IDs. The resulting items are concatenated into a single string, 
							separating each item with a comma.

							If the @AddRem parameter is 'ADD' and the @Val parameter is a valid object ID, the function appends the @Val 
							parameter to the end of the resulting string, separated by a comma if necessary.

							Finally, the function returns the modified string as the output of the function.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[RemAddObjFromList]
(
    -- Add the parameters for the function here
    @List NVARCHAR(4000),
    @Val NVARCHAR(255),
    @AddRem NVARCHAR(255) = 'ADD'
)
RETURNS VARCHAR(4000)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(4000) = N'';

    SELECT @rValue = @rValue + N',' + Item
    FROM [flw].[StringSplit](@List, ',')
    WHERE Item <> @Val
          AND OBJECT_ID(Item) IS NOT NULL;

    IF (@AddRem = 'ADD' AND OBJECT_ID(@Val) IS NOT NULL)
    BEGIN
        SET @rValue = @rValue + IIF(LEN(@rValue) > 0, ',', '') + @Val;
    END;

    --Return the result of the function
    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[RemAddObjFromList] is to remove or add a value to a comma-separated list of strings.
  -- Summary			:	The function takes three input parameters: @List, which is the comma-separated list of strings being modified, @Val, 
							which is the value being removed or added to the list, and @AddRem, which is the operation being performed.

							The function splits the @List parameter into individual items using the [flw].[StringSplit] function and removes any 
							items that match the @Val parameter or are not valid object IDs. The resulting items are concatenated into a single string, 
							separating each item with a comma.

							If the @AddRem parameter is ''ADD'' and the @Val parameter is a valid object ID, the function appends the @Val 
							parameter to the end of the resulting string, separated by a comma if necessary.

							Finally, the function returns the modified string as the output of the function.', 'SCHEMA', N'flw', 'FUNCTION', N'RemAddObjFromList', NULL, NULL
GO
