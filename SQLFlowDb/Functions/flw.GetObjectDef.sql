SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetObjectName]
  -- Date				:   2023-09-22
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-09-22		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetObjectDef]
(
    -- Add the parameters for the function here
    @ObjectMK int
)
RETURNS NVARCHAR(max)
AS
BEGIN
    -- Declare the return variable here
    -- Return the result of the function

	DECLARE @FormattedSQL NVARCHAR(MAX) ;
    DECLARE @Position INT = CHARINDEX(',', @FormattedSQL);
    
	SELECT @FormattedSQL = CASE WHEN LEN([ObjectDef])> 0 THEN [ObjectDef] ELSE ObjectName END
	FROM [flw].[LineageObjectMK]
	WHERE ObjectMK = @ObjectMK
    
	 SET @FormattedSQL = REPLACE(@FormattedSQL, ',', ',' + CHAR(13) + CHAR(10));

	 SET @FormattedSQL = REPLACE(@FormattedSQL, CHAR(13) + CHAR(10) +CHAR(13) + CHAR(10),  CHAR(13) + CHAR(10));

    RETURN '<pre>' + @FormattedSQL + '</pre>'
	
	
END;
GO
