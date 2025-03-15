SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetLogSection]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function is to generate a formatted log message for a specific section of a process, including a section header and body text.
  -- Summary			:	The function takes two input parameters: the name of the section and the text body for that section. 
  
							It returns a formatted string with the section header and body, indented based on the current nesting level. 
							The log message can be used for debugging or monitoring purposes in a data warehouse solution.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[GetLogSection] (@Section NVARCHAR(1024),
                                      @Body NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
BEGIN

    DECLARE @LogStack NVARCHAR(MAX);
    --+ ' ***********************************' 
    SET @LogStack =  REPLICATE (' ' , @@NESTLEVEL*2) + N'--***** ' + @Section + N' *****' + CHAR(13) + CHAR(10) + @Body + CHAR(13) + CHAR(10) + CHAR(13) + CHAR(10);

    RETURN @LogStack;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function is to generate a formatted log message for a specific section of a process, including a section header and body text.
  -- Summary			:	The function takes two input parameters: the name of the section and the text body for that section. 
  
							It returns a formatted string with the section header and body, indented based on the current nesting level. 
							The log message can be used for debugging or monitoring purposes in a data warehouse solution.', 'SCHEMA', N'flw', 'FUNCTION', N'GetLogSection', NULL, NULL
GO
