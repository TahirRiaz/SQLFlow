SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[WKTWIDTH]
  -- Date				:   22.11.2019
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This stored procedures 
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		22.11.2019		Initial
  ##################################################################################################################################################
*/

CREATE FUNCTION [flw].[WKTWIDTH]
(
    @String NVARCHAR(MAX) -- SRC WKT String

)
RETURNS FLOAT
AS
BEGIN;
    DECLARE @returnValue FLOAT;

    DECLARE @str NVARCHAR(MAX);
    SET @str = REPLACE(REPLACE(STUFF(@String, 1, PATINDEX('%[0-9 ]%', @String) - 1, ''), '(', ''), ')', '');

    SELECT @returnValue = MAX(CAST(LTRIM(RTRIM(a.Item)) AS FLOAT)) - MIN(CAST(LTRIM(RTRIM(a.Item)) AS FLOAT))
    FROM [flw].[List2Tbl](@str, ',') m
        CROSS APPLY
    (SELECT Item FROM [flw].[List2Tbl](m.Item, ' ') WHERE RecID = 1) a;

    RETURN @returnValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This stored procedures', 'SCHEMA', N'flw', 'FUNCTION', N'WKTWIDTH', NULL, NULL
GO
