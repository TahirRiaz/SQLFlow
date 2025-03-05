SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetObjectName]
  -- Date				:   2023-09-20
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   
  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-09-20		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetObjectName]
(
    -- Add the parameters for the function here
    @FullName NVARCHAR(255)
)
RETURNS NVARCHAR(255)
AS
BEGIN
    -- Declare the return variable here
    -- Return the result of the function
    RETURN PARSENAME(@FullName,2) +'.'+ PARSENAME(@FullName,1);

END;
GO
