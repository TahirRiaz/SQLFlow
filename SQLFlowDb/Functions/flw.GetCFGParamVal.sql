SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetCFGParamVal]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The user-defined function [flw].[GetCFGParamVal] retrieves the value of a configuration parameter 
							specified by its name from a system configuration table (flw.SysCFG).
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetCFGParamVal]
(
    -- Add the parameters for the function here

    @ParamName NVARCHAR(255)
)
RETURNS NVARCHAR(255)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(255);
    SELECT @rValue = ParamValue
    FROM flw.SysCFG
    WHERE (ParamName = @ParamName);

    -- Return the result of the function
    RETURN @rValue;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The user-defined function [flw].[GetCFGParamVal] retrieves the value of a configuration parameter 
							specified by its name from a system configuration table (flw.SysCFG).', 'SCHEMA', N'flw', 'FUNCTION', N'GetCFGParamVal', NULL, NULL
GO
