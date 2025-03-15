SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetDateTimeFormat]
  -- Date				:   2023.11.05
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   Fetches various  DateTime Formats
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023.11.05		Initial
  ##################################################################################################################################################
*/

CREATE PROCEDURE [flw].[GetDateTimeFormat]
AS
BEGIN

    SELECT [Format], [FormatLength]
    FROM [flw].[SysDateTimeFormat]
	--WHERE [Format] = 'yyyy_M_d'
    ORDER BY [FormatLength] DESC;

END;
GO
