SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[GetSQLFlowDataTables]
  -- Date				:   2022-12-26
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of the provided user-defined function [flw].[GetSQLFlowDataTables] is to retrieve the list of base tables in the SQL Server database.
  -- Summary			:	The function returns a comma-separated list of table schema and table names as an NVARCHAR(4000) string. 
							The list of tables is obtained by querying the information_schema.tables system view to retrieve all base tables in the database, 
							excluding tables with specific names such as LineageEdge, LineageMap, SysFlowDep, SysLog, SysLogBatch, SysLogFile, and SysStats. 
							The resulting list of table names is then concatenated into a single string using the '+' operator and a comma delimiter. 
							The function removes the first comma from the concatenated string before returning it as output.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-12-26		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[GetSQLFlowDataTables] ()
-- Add the parameters for the function here
RETURNS NVARCHAR(4000)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @TableList  [NVARCHAR](4000) = '';

	SELECT @TableList =  @TableList +',' + Table_Schema +'.'+ TABLE_NAME 
	FROM information_schema.tables
	WHERE Table_Type = 'Base Table'
	AND Table_Name NOT IN ('LineageEdge','LineageMap','SysFlowDep','SysLog','SysLogBatch','SysLogFile','SysStats')
	ORDER BY Table_Name

	SET @TableList = SUBSTRING(@TableList,2,LEN(@TableList))

    -- Return the result of the function
    RETURN @TableList;

END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[GetSQLFlowDataTables] is to retrieve the list of base tables in the SQL Server database.
  -- Summary			:	The function returns a comma-separated list of table schema and table names as an NVARCHAR(4000) string. 
							The list of tables is obtained by querying the information_schema.tables system view to retrieve all base tables in the database, 
							excluding tables with specific names such as LineageEdge, LineageParsed, SysFlowDep, SysLog, SysLogBatch, SysLogFile, and SysStats. 
							The resulting list of table names is then concatenated into a single string using the ''+'' operator and a comma delimiter. 
							The function removes the first comma from the concatenated string before returning it as output.', 'SCHEMA', N'flw', 'FUNCTION', N'GetSQLFlowDataTables', NULL, NULL
GO
