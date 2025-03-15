SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetADOSourceName]
  -- Date				:   2022-11-13
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This user-defined function retrieves the name of the source object for a given flow ID in the pre-ingestion ADO metadata table.
  -- Summary			:	The function takes a single input parameter, which is the FlowID of the pre-ingestion ADO metadata record for the desired source object. 
  
							It then queries the metadata table to retrieve the names of the source database, schema, and object, 
							and concatenates them into a fully qualified object name in the format of [database].[schema].[object]. 
							The function then returns this fully qualified name as its output.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-11-13		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetADOSourceName] (
-- Add the parameters for the function here
@FlowID INT)
RETURNS NVARCHAR(255)
AS
BEGIN

    DECLARE @rValue NVARCHAR(255);

    SELECT @rValue
        = CASE
               WHEN LEN(ISNULL(srcDatabase, '')) > 0 THEN '[' + [flw].[RemBrackets](srcDatabase) + '].'
               ELSE '' END + CASE
                                  WHEN LEN(ISNULL(srcSchema, '')) > 0 THEN '[' + [flw].[RemBrackets](srcSchema) + '].'
                                  ELSE '' END
          + CASE
                 WHEN LEN(ISNULL(srcObject, '')) > 0 THEN '[' + [flw].[RemBrackets](srcObject) + ']'
                 ELSE '' END
      FROM [flw].[PreIngestionADO]
	  WHERE FlowID = @FlowID

	  return @rValue
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This user-defined function retrieves the name of the source object for a given flow ID in the pre-ingestion ADO metadata table.
  -- Summary			:	The function takes a single input parameter, which is the FlowID of the pre-ingestion ADO metadata record for the desired source object. 
  
							It then queries the metadata table to retrieve the names of the source database, schema, and object, 
							and concatenates them into a fully qualified object name in the format of [database].[schema].[object]. 
							The function then returns this fully qualified name as its output.', 'SCHEMA', N'flw', 'FUNCTION', N'GetADOSourceName', NULL, NULL
GO
