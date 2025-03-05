SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
  
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[ProcNameCleanup]
  -- Date				:   2021.04.26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   Cleanup SP Process value. removes exec and ;
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2021.04.26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[ProcNameCleanup] 
(
	-- Add the parameters for the function here
	@ObjectName nvarchar(255)
)
RETURNS nvarchar(255)
AS
BEGIN
	SELECT @ObjectName = REPLACE(@ObjectName,char(9),'')
    SELECT @ObjectName = REPLACE(@ObjectName,char(13),'')
    SELECT @ObjectName = REPLACE(@ObjectName,char(10),'')
    SELECT @ObjectName = REPLACE(@ObjectName,N'‬','')
    SELECT @ObjectName = REPLACE(@ObjectName,N'exec‬','')
	SELECT @ObjectName = REPLACE(@ObjectName,N';‬','')

	-- Declare the return variable here
	return ltrim(rtrim(flw.GetValidSrcTrgName(@ObjectName)))

END
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   Cleanup SP Process value. removes exec and ;', 'SCHEMA', N'flw', 'FUNCTION', N'ProcNameCleanup', NULL, NULL
GO
