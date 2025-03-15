SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[BuildVirtualColDS]
  -- Date				:   2022-10-28
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The stored procedure flw.BuildVirtualColDS retrieves metadata for virtual columns from two different tables,
							[flw].[Ingestion] and flw.PreIngestionCSV. This information is useful when you need to create or manipulate 
							virtual columns in your ETL or data processing pipeline.
  -- Summary			:	The stored procedure performs the following actions:

							It sets the NOCOUNT option to ON, which prevents the display of the row count for each statement. 
							This is useful when you want to suppress the output of additional result sets that may interfere with SELECT statements.

							It executes a SELECT statement on the [flw].[Ingestion] table, where the SysAlias column has the value 'APCDalanepP2P'. The statement retrieves the FlowID, 
							a hard-coded column name ([MK_SourceSystem]), data type (tinyint), data type expression (tinyint), and a select expression (CAST(37 as tinyint)).

							It executes another SELECT statement on the flw.PreIngestionCSV table, returning the FlowID, hard-coded values for FlowType (csv) and Virtual (1), 
							and other column-related information like the column name, select expression, column alias, sort order, and whether the column should be excluded from the view.

							The result sets from these SELECT statements can be used to create or manipulate virtual columns, which can be helpful in processing or transforming data.

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022-10-28		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[BuildVirtualColDS]
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON

    -- Insert statements for procedure here
    SELECT FlowID,
       '[MK_SourceSystem]' AS ColumnName,
       'tinyint' AS DataType,
       'tinyint' AS DataTypeExp,
       'CAST(37 as tinyint)' AS SelectExp
	FROM [flw].[Ingestion]
	WHERE SysAlias = N'APCDalanepP2P';


	SELECT FlowID,
		   'csv' AS FlowType,
		   1 AS Virtual,
		   '[SourceSystemMK]' AS ColName,
		   'CAST(37 as tinyint)' AS SelectExp,
		   NULL AS ColAlias,
		   NULL AS SortOrder,
		   0 AS ExcludeColFromView
	  FROM flw.PreIngestionCSV;
END
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The stored procedure flw.BuildVirtualColDS retrieves metadata for virtual columns from two different tables,
							[flw].[Ingestion] and flw.PreIngestionCSV. This information is useful when you need to create or manipulate 
							virtual columns in your ETL or data processing pipeline.
  -- Summary			:	The stored procedure performs the following actions:

							It sets the NOCOUNT option to ON, which prevents the display of the row count for each statement. 
							This is useful when you want to suppress the output of additional result sets that may interfere with SELECT statements.

							It executes a SELECT statement on the [flw].[Ingestion] table, where the SysAlias column has the value ''APCDalanepP2P''. The statement retrieves the FlowID, 
							a hard-coded column name ([MK_SourceSystem]), data type (tinyint), data type expression (tinyint), and a select expression (CAST(37 as tinyint)).

							It executes another SELECT statement on the flw.PreIngestionCSV table, returning the FlowID, hard-coded values for FlowType (csv) and Virtual (1), 
							and other column-related information like the column name, select expression, column alias, sort order, and whether the column should be excluded from the view.

							The result sets from these SELECT statements can be used to create or manipulate virtual columns, which can be helpful in processing or transforming data.', 'SCHEMA', N'flw', 'PROCEDURE', N'BuildVirtualColDS', NULL, NULL
GO
