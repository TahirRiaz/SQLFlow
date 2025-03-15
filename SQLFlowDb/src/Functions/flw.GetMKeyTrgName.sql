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
  -- Purpose			:   
  -- Summary			:								

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2024-06-30		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetMKeyTrgName] 
(
	-- Add the parameters for the function here
	@TableName nvarchar(255),
	@MatchKeyID int
)
RETURNS nvarchar(255)
AS
BEGIN
	-- Declare the return variable here
	DECLARE @rValue nvarchar(255)


	set @rValue = '['+parsename(@TableName,3)+'].['+[flw].[GetCFGParamVal]('Schema14Mkey')+'].['+parsename(@TableName,1)+ CAST(@MatchKeyID AS VARCHAR(150)) + ']'	

	-- Return the result of the function
	RETURN @rValue
END
GO
