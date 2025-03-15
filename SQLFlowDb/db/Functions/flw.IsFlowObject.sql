SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[IsFlowObject]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[IsFlowObject] is to check if a given object has a flow associated with it.
  -- Summary			:	The function takes an input parameter @ObjectMK, which is an integer representing the unique identifier of the object being checked. 
							The function queries the [flw].[LineageObjectMK] table to check if the specified object has an associated flow 
							by looking up the value of the [IsFlowObject] column for the given @ObjectMK. 
							
							If the value of [IsFlowObject] is null, the function returns 0 indicating that the object is not associated with any flow. 
							Otherwise, the function returns the value of [IsFlowObject] which indicates whether or not the object is associated with a flow. 
							The returned value is of type bit, with 1 indicating that the object is associated with a flow, and 0 indicating that it is not.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[IsFlowObject] (@ObjectMK int)
RETURNS bit
BEGIN

    DECLARE @rValue bit;

	SELECT @rValue = [IsFlowObject] FROM [flw].[LineageObjectMK] WHERE [ObjectMK] = @ObjectMK

	if(@rValue is null)
		set @rValue = 0

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[IsFlowObject] is to check if a given object has a flow associated with it.
  -- Summary			:	The function takes an input parameter @ObjectMK, which is an integer representing the unique identifier of the object being checked. 
							The function queries the [flw].[LineageObjectMK] table to check if the specified object has an associated flow 
							by looking up the value of the [IsFlowObject] column for the given @ObjectMK. 
							
							If the value of [IsFlowObject] is null, the function returns 0 indicating that the object is not associated with any flow. 
							Otherwise, the function returns the value of [IsFlowObject] which indicates whether or not the object is associated with a flow. 
							The returned value is of type bit, with 1 indicating that the object is associated with a flow, and 0 indicating that it is not.', 'SCHEMA', N'flw', 'FUNCTION', N'IsFlowObject', NULL, NULL
GO
