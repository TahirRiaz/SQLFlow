SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[FormListWithBrackets]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The function returns a formatted nvarchar(4000) value with the input list enclosed in brackets with each value separated by a comma.
  -- Summary			:	First, the function removes any existing brackets from the input list by calling the [flw].[RemBrackets] function. 
							It then uses the [flw].[StringSplit] function to split the input list into individual values, 
							and then concatenates each value with a comma and an opening and closing square bracket.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[FormListWithBrackets]
(
	-- Add the parameters for the function here
	@List nvarchar(4000),
	@delimmiter char(1)
)
RETURNS nvarchar(4000)
AS
BEGIN
	-- Declare the return variable here
	DECLARE @rValue nvarchar(4000) = ''

	set @List = [flw].[RemBrackets](@List) 
	
	SELECT @rValue = @rValue +',[' +Item + ']'
         FROM [flw].[StringSplit](@List, @delimmiter) 

	set @rValue = substring(@rValue,2,len(@rValue))
	-- Add the T-SQL statements to compute the return value here
	

	-- Return the result of the function
	RETURN @rValue

END
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The function returns a formatted nvarchar(4000) value with the input list enclosed in brackets with each value separated by a comma.
  -- Summary			:	First, the function removes any existing brackets from the input list by calling the [flw].[RemBrackets] function. 
							It then uses the [flw].[StringSplit] function to split the input list into individual values, 
							and then concatenates each value with a comma and an opening and closing square bracket.', 'SCHEMA', N'flw', 'FUNCTION', N'FormListWithBrackets', NULL, NULL
GO
