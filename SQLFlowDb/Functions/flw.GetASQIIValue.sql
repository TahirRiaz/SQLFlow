SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetASQIIValue]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function is to return the ASCII value of the first character of the input string if 
							it is not a numeric value, and the input value itself if it is a numeric value. If the input string is empty or null, 
							it returns '0'.
  -- Summary			:	The function takes a single parameter, a nvarchar value, and returns a varchar value. 
							If the input string is numeric, it returns the input value, otherwise it returns the ASCII value of the first character 
							of the input string. If the input string is empty or null, it returns '0'.
  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetASQIIValue]
(
    -- Add the parameters for the function here
    @Value NVARCHAR(25)
)
RETURNS VARCHAR(25)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(25) = N'0';

    -- Add the T-SQL statements to compute the return value here
    IF (LEN(ISNULL(@Value, '')) > 0)
    BEGIN
        IF (
               ISNUMERIC(@Value) = 1
               AND @Value NOT IN ( ',', '.', '\', '$', '+', '-', '£' )
           )
        BEGIN
            SET @rValue = @Value;
        END;
        ELSE
        BEGIN
            SET @rValue = ASCII(SUBSTRING(@Value, 1, 1));
        END;
    END;
    ELSE
    BEGIN
        SET @rValue = N'0';
    END;

    -- Return the result of the function
    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function is to return the ASCII value of the first character of the input string if 
							it is not a numeric value, and the input value itself if it is a numeric value. If the input string is empty or null, 
							it returns ''0''.
  -- Summary			:	The function takes a single parameter, a nvarchar value, and returns a varchar value. 
							If the input string is numeric, it returns the input value, otherwise it returns the ASCII value of the first character 
							of the input string. If the input string is empty or null, it returns ''0''.', 'SCHEMA', N'flw', 'FUNCTION', N'GetASQIIValue', NULL, NULL
GO
