SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetValidSrcTrgName]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[GetValidSrcTrgName] is to format a table name into a valid source or target table name that can be used in SQL Server queries. 
  -- Summary			:	The function takes an input parameter @TableName, which is the name of the table to be formatted. 
							The table name is parsed using the T-SQL built-in function PARSENAME to retrieve the database name, 
							schema name, and table name as separate components. 
							
							The function then concatenates the components into a single string that represents the fully qualified name of the table with 
							square brackets around each component to ensure that any special characters or reserved words in the names are properly escaped. 
							The resulting formatted table name is returned as the output of the function.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetValidSrcTrgName] 
(
	-- Add the parameters for the function here
	@TableName nvarchar(255)
)
RETURNS nvarchar(255)
AS
BEGIN
	-- Declare the return variable here
	DECLARE @rValue nvarchar(255)

	set @rValue = '['+parsename(@TableName,3)+'].['+parsename(@TableName,2)+'].['+parsename(@TableName,1)+']'	

	-- Return the result of the function
	RETURN @rValue
END

GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[GetValidSrcTrgName] is to format a table name into a valid source or target table name that can be used in SQL Server queries. 
  -- Summary			:	The function takes an input parameter @TableName, which is the name of the table to be formatted. 
							The table name is parsed using the T-SQL built-in function PARSENAME to retrieve the database name, 
							schema name, and table name as separate components. 
							
							The function then concatenates the components into a single string that represents the fully qualified name of the table with 
							square brackets around each component to ensure that any special characters or reserved words in the names are properly escaped. 
							The resulting formatted table name is returned as the output of the function.', 'SCHEMA', N'flw', 'FUNCTION', N'GetValidSrcTrgName', NULL, NULL
GO
