SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[StrRemRegex]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The StrRemRegex function removes any characters from the input string that match a specified regular expression pattern and returns the cleaned up string.
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[StrRemRegex]
(
    @InputValue VARCHAR(1000),
    @Pattern VARCHAR(1000) = '%[^a-z]%'
)
RETURNS VARCHAR(1000)
AS
BEGIN

    DECLARE @KeepValues AS VARCHAR(50);
    SET @KeepValues = @Pattern;
    WHILE PATINDEX(@KeepValues, @InputValue) > 0
    SET @InputValue = STUFF(@InputValue, PATINDEX(@KeepValues, @InputValue), 1, '');

    RETURN @InputValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The StrRemRegex function removes any characters from the input string that match a specified regular expression pattern and returns the cleaned up string.', 'SCHEMA', N'flw', 'FUNCTION', N'StrRemRegex', NULL, NULL
GO
