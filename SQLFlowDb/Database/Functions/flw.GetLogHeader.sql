SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetLogHeader]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this user-defined function is to create a log header in a formatted way. 
  -- Summary			:	This user-defined function takes two input parameters:

							@Header: a nvarchar(1024) that specifies the header for the log.

							@Body: a nvarchar(MAX) that specifies the body of the log.

							The function concatenates the header and the body with a set of formatting characters and returns the formatted log header as an nvarchar(MAX).

							The formatting characters include indentations, line breaks, and a set of special characters to indicate the start and end of the log header.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/


CREATE FUNCTION [flw].[GetLogHeader] (@Header NVARCHAR(1024),
                                     @Body NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
BEGIN

    DECLARE @LogStack NVARCHAR(MAX);

    SET @LogStack
        = REPLICATE (' ' , @@NESTLEVEL*2) + N'--################################### ' + @Header + N' ###################################' + CHAR(13)
          + CHAR(10) + @Body + CHAR(13) + CHAR(10) + CHAR(13) + CHAR(10);

    RETURN @LogStack;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this user-defined function is to create a log header in a formatted way. 
  -- Summary			:	This user-defined function takes two input parameters:

							@Header: a nvarchar(1024) that specifies the header for the log.

							@Body: a nvarchar(MAX) that specifies the body of the log.

							The function concatenates the header and the body with a set of formatting characters and returns the formatted log header as an nvarchar(MAX).

							The formatting characters include indentations, line breaks, and a set of special characters to indicate the start and end of the log header.', 'SCHEMA', N'flw', 'FUNCTION', N'GetLogHeader', NULL, NULL
GO
