SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[WKT4STRING]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   WKT vector for a given string
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[WKT4STRING] (
       @text varchar(100), -- a string of ASCII characters to convert to WKT
       @XScale   float,   -- horizontal scale (defines the width of a single character)
       @XOrigin   float,  -- horizontal origin (X coordinate of the lower left corner of the string on the grid)
       @YScale   float,   -- -- horizontal Origin
       @YOrigin   float         -- vertial origin (Y coordinate of the lower left corner of the string on the grid)
)
RETURNS VARCHAR(max)
AS
BEGIN;
       DECLARE @n int, @len int, @g varchar(max), @l varchar(max);
       SET @len = LEN( @text );
       SET @n = 0;
       WHILE @n < @len BEGIN;
              SET @n = @n + 1;
              SET @l = [flw].[WKT4ASCII]( SUBSTRING( @text, @n, 1 ), @XScale, @XOrigin, @YScale, @YOrigin );
              IF @l IS NOT NULL SET @g = COALESCE( @g + ',', '' ) + @l;
              SET @XOrigin = @XOrigin + .571 * @XScale;
       END;
       RETURN( @g );
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   WKT vector for a given string', 'SCHEMA', N'flw', 'FUNCTION', N'WKT4STRING', NULL, NULL
GO
