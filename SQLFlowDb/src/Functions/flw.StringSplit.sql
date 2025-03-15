SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[StringSplit]
  -- Date				:   2023-01-20
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   [flw].[StringSplit] splits a string into a table of substrings based on a specified separator.
  -- Summary			:	The function takes two input parameters:

							@string: a string to be split
							@separator: a string used as a separator to split the input string
							The function uses a series of Common Table Expressions (CTEs) to generate a table of sequential numbers, which is then used to split the input string. 
							Specifically, the T(N) CTE generates a table of sequential numbers from 0 to the length of the input string minus 1. 
							
							The Delim(Pos) CTE uses the LIKE operator to identify the positions of the separator string in the input string. 
							The Separated(value) CTE then uses the positions of the separator to extract the individual substrings from the input string.

							Finally, the SELECT statement returns a table of substrings with two columns:

							Item: the individual substring
							Ordinal: the sequence number of the substring in the output table.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-01-20		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[StringSplit]
(
    @string    NVARCHAR(MAX), 
    @separator NVARCHAR(MAX)
	
)
RETURNS TABLE WITH SCHEMABINDING 
AS RETURN
   WITH X(N) AS (SELECT 'Table1' FROM (VALUES (0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0),(0)) T(C)),
        Y(N) AS (SELECT 'Table2' FROM X A1, X A2, X A3, X A4, X A5, X A6, X A7, X A8) , -- Up to 16^8 = 4 billion
        T(N) AS (SELECT TOP(ISNULL(LEN(@string),0)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) -1 N FROM Y),
        Delim(Pos) AS (SELECT t.N FROM T WHERE (SUBSTRING(@string, t.N, LEN(@separator+'x')-1) LIKE @separator OR t.N = 0)),
        Separated(value) AS (SELECT SUBSTRING(@string, d.Pos + LEN(@separator+'x')-1, LEAD(d.Pos,1,2147483647) OVER (ORDER BY (SELECT NULL)) - d.Pos - LEN(@separator))
                               FROM Delim d
                              WHERE @string IS NOT NULL)
       
	   SELECT LTRIM(RTRIM(s.value)) AS Item, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Ordinal 
         FROM Separated s
        WHERE s.value <> @separator
		AND s.value <> ''
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   [flw].[StringSplit] splits a string into a table of substrings based on a specified separator.
  -- Summary			:	The function takes two input parameters:

							@string: a string to be split
							@separator: a string used as a separator to split the input string
							The function uses a series of Common Table Expressions (CTEs) to generate a table of sequential numbers, which is then used to split the input string. 
							Specifically, the T(N) CTE generates a table of sequential numbers from 0 to the length of the input string minus 1. 
							
							The Delim(Pos) CTE uses the LIKE operator to identify the positions of the separator string in the input string. 
							The Separated(value) CTE then uses the positions of the separator to extract the individual substrings from the input string.

							Finally, the SELECT statement returns a table of substrings with two columns:

							Item: the individual substring
							Ordinal: the sequence number of the substring in the output table.', 'SCHEMA', N'flw', 'FUNCTION', N'StringSplit', NULL, NULL
GO
