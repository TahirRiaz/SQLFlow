SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[CheckUniqueSysTargetName]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   Checks if same target name is used accross diffrence sources. 
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[CheckUniqueSysTargetName] (@SysAlias nvarchar(70), @trgServer nvarchar(250), @trgDBSchTbl nvarchar(250))
RETURNS int
AS 
BEGIN
  DECLARE @retval int
    
	--Check if same target name is used accross diffrence sources. Can result schema sync problems.
	SELECT @retval = CASE WHEN count(*) > 0 THEN 1 ELSE 0 END
    FROM [flw].[Ingestion]
    WHERE SysAlias <> @SysAlias 
	AND trgServer = @trgServer
	AND [flw].[RemBrackets](trgDBSchTbl) = [flw].[RemBrackets](@trgDBSchTbl)

  RETURN @retval
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   Checks if same target name is used accross diffrence sources. ', 'SCHEMA', N'flw', 'FUNCTION', N'CheckUniqueSysTargetName', NULL, NULL
GO
