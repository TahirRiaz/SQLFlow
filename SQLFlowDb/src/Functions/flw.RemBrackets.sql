SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
-- User Defined Function

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[RemBrackets]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This user-defined function removes any square brackets from the input string "@ObjectName". 
							It also removes several other Unicode characters and replaces the Persian and Arabic numeral characters with their Latin equivalents.
							Finally, it trims any leading or trailing spaces from the resulting string and returns it.
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[RemBrackets]
(
    -- Add the parameters for the function here
    @ObjectName NVARCHAR(max)
)
RETURNS NVARCHAR(max)
AS
BEGIN
    -- Declare the return variable here
    --SET @ObjectName = ISNULL(@ObjectName, '');
    --SET @ObjectName = REPLACE(@ObjectName, N'َ', '');
    --SET @ObjectName = REPLACE(@ObjectName, CHAR(9), '');
    --SET @ObjectName = REPLACE(@ObjectName, CHAR(13), '');
    --SET @ObjectName = REPLACE(@ObjectName, CHAR(10), '');
    --SET @ObjectName = REPLACE(@ObjectName, N'‬', '');
    --SET @ObjectName = REPLACE(@ObjectName, N'‬', '');
    --SET @ObjectName = REPLACE(@ObjectName, N'‬‬', '');
    --SET @ObjectName = REPLACE(@ObjectName, N'‎', ''); --its a hidden character
    --SET @ObjectName = REPLACE(@ObjectName, N'‎', ''); --ltr code
    --SET @ObjectName = REPLACE(@ObjectName, N'‎', ''); --rtl code
    --SET @ObjectName = REPLACE(@ObjectName, N'۰', '0');
    --SET @ObjectName = REPLACE(@ObjectName, N'۱', '1');
    --SET @ObjectName = REPLACE(@ObjectName, N'۲', '2');
    --SET @ObjectName = REPLACE(@ObjectName, N'۳', '3');
    --SET @ObjectName = REPLACE(@ObjectName, N'۴', '4');
    --SET @ObjectName = REPLACE(@ObjectName, N'۵', '5');
    --SET @ObjectName = REPLACE(@ObjectName, N'۶', '6');
    --SET @ObjectName = REPLACE(@ObjectName, N'۷', '7');
    --SET @ObjectName = REPLACE(@ObjectName, N'۸', '8');
    --SET @ObjectName = REPLACE(@ObjectName, N'۹', '9');
    --SET @ObjectName = REPLACE(@ObjectName, N'٠', '0');
    --SET @ObjectName = REPLACE(@ObjectName, N'١', '1');
    --SET @ObjectName = REPLACE(@ObjectName, N'٢', '2');
    --SET @ObjectName = REPLACE(@ObjectName, N'٣', '3');
    --SET @ObjectName = REPLACE(@ObjectName, N'٤', '4');
    --SET @ObjectName = REPLACE(@ObjectName, N'٥', '5');
    --SET @ObjectName = REPLACE(@ObjectName, N'٦', '6');
    --SET @ObjectName = REPLACE(@ObjectName, N'٧', '7');
    --SET @ObjectName = REPLACE(@ObjectName, N'٨', '8');
    --SET @ObjectName = REPLACE(@ObjectName, N'٩', '9');

    SET @ObjectName = REPLACE(REPLACE(@ObjectName, ']', ''), '[', '');
    SET @ObjectName = LTRIM(RTRIM(@ObjectName));

    RETURN @ObjectName;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This user-defined function removes any square brackets from the input string "@ObjectName". 
							It also removes several other Unicode characters and replaces the Persian and Arabic numeral characters with their Latin equivalents.
							Finally, it trims any leading or trailing spaces from the resulting string and returns it.', 'SCHEMA', N'flw', 'FUNCTION', N'RemBrackets', NULL, NULL
GO
