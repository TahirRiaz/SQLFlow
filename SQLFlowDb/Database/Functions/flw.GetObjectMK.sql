SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetObjectMK]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function is to retrieve the ObjectMK (an identifier for a lineage object) for a given ObjectName and SysAlias.
  -- Summary			:	If a SysAlias is provided, it will be used to narrow the search, but if no matches are found, the function will search for a match using only the ObjectName. 
							If a match is not found, NULL is returned. The function returns an integer value.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetObjectMK] (@ObjectName NVARCHAR(1024), @SysAlias nvarchar(70))
RETURNS int
BEGIN

    DECLARE @rValue int;

	SELECT @rValue = [ObjectMK] FROM flw.LineageObjectMK WHERE ObjectName = @ObjectName and [SysAlias] = @SysAlias

	--If no matches are found with SysAlias
	if(@rValue is null)
	begin
		SELECT @rValue = [ObjectMK] FROM flw.LineageObjectMK WHERE ObjectName = @ObjectName 
	END

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function is to retrieve the ObjectMK (an identifier for a lineage object) for a given ObjectName and SysAlias.
  -- Summary			:	If a SysAlias is provided, it will be used to narrow the search, but if no matches are found, the function will search for a match using only the ObjectName. 
							If a match is not found, NULL is returned. The function returns an integer value.', 'SCHEMA', N'flw', 'FUNCTION', N'GetObjectMK', NULL, NULL
GO
