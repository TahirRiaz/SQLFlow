SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[StrCleanup]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The StrCleanup function removes any invalid characters from the input string and returns the cleaned up string. 
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[StrCleanup]
(
    @src NVARCHAR(MAX)
)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @out NVARCHAR(MAX);
    DECLARE @InvalidChar VARCHAR(100); -- less then char 31
    SET @out = @src;
    DECLARE @increment INT = 1;
    WHILE @increment <= DATALENGTH(@out)
    BEGIN
        IF (ASCII(SUBSTRING(@out, @increment, 1)) < 31)
        BEGIN
            SET @InvalidChar = CHAR(ASCII(SUBSTRING(@out, @increment, 1)));
            SET @out = REPLACE(@out, @InvalidChar, '');
        END;
        SET @increment = @increment + 1;
    END;

    -- Return the result of the function
    RETURN @out;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The StrCleanup function removes any invalid characters from the input string and returns the cleaned up string.', 'SCHEMA', N'flw', 'FUNCTION', N'StrCleanup', NULL, NULL
GO
