SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
/*
  ##################################################################################################################################################
  -- Name				:   [flw].[SearchFiles]
  -- Date				:   2023-03-14
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   The purpose of this stored procedure is to retrieve flow file information based on the provided search parameter. 
  -- Summary			:	Check if the provided @Search parameter is numeric:

							If @Search is numeric, it is assumed to be a flow ID. The stored procedure [flw].[GetFlowType] is called to get the flow type.

							If the flow type is 'exp', the procedure retrieves the top 5 records from [flw].[SysLogExport] table filtered by the flow ID, 
							followed by a query to group and retrieve the maximum export date for each file in the flow.

							If the flow type is not 'exp', the procedure retrieves the top 5 records from flw.SysLogFile table filtered by the flow ID, 
							followed by a query to group and retrieve the maximum file row date for each file in the flow.

							If @Search is not numeric, it is assumed to be a partial file name. The procedure retrieves all records from the flw.SysLogFile table 
							where the file name contains the search string, ordered by the file date in descending order.


  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2023-03-14		Initial
  ##################################################################################################################################################
*/
CREATE PROCEDURE [flw].[SearchFiles]
(
    -- Add the parameters for the stored procedure here
    @Search NVARCHAR(255)
)
AS
BEGIN
    -- SET NOCOUNT ON added to prevent extra result sets from
    -- interfering with SELECT statements.
    SET NOCOUNT ON;

    IF (ISNUMERIC(@Search) = 1)
    BEGIN
        DECLARE @FlowType NVARCHAR(255);

		SELECT @FlowType = [flw].[GetFlowType](@Search);

		IF (@FlowType = 'exp')
        BEGIN
            SELECT TOP 5
                   FlowID,
                   FileName_DW,
                   [FilePath_DW],
                   [FileName_DW] AS IngestionDate
            FROM [flw].[SysLogExport]
            WHERE (FlowID = @Search)
            ORDER BY [FileName_DW] DESC;

            SELECT FlowID,
                   [FilePath_DW],
                   [FileName_DW],
                   MAX([ExportDate]) AS IngestionDate
            FROM [flw].[SysLogExport]
            GROUP BY FlowID,
                     [FilePath_DW],
                     [FileName_DW]
            HAVING (FlowID = @Search)
            ORDER BY [FileName_DW] DESC;

        END;
        ELSE
        BEGIN
            SELECT TOP 5
                   FlowID,
                   FileName_DW,
                   FileDate_DW,
                   [FileSize_DW],
                   FileRowDate_DW AS IngestionDate
            FROM flw.SysLogFile
            WHERE (FlowID = @Search)
            ORDER BY FileRowDate_DW DESC;

            SELECT FlowID,
                   FileName_DW,
                   FileDate_DW,
                   [FileSize_DW],
                   MAX(FileRowDate_DW) AS IngestionDate
            FROM flw.SysLogFile
            GROUP BY FlowID,
                     FileDate_DW,
                     FileName_DW,
                     [FileSize_DW]
            HAVING (FlowID = @Search)
            ORDER BY FileDate_DW DESC;

        END;
    END;
    ELSE
    BEGIN
        SELECT FlowID,
               FileName_DW,
               FileDate_DW,
               [FileSize_DW],
               FileRowDate_DW AS IngestionDate
        FROM flw.SysLogFile
        WHERE (FileName_DW LIKE '%' + @Search +'%')
        ORDER BY FileDate_DW DESC;
    END;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of this stored procedure is to retrieve the flow files and their related information based on the given FlowID. 
  -- Summary			:	Here''s an overview of how the stored procedure works:

							It begins by setting NOCOUNT to ON to prevent the display of the row count after each SELECT statement.

							It declares a variable called @FlowType of type NVARCHAR(255).

							The stored procedure retrieves the flow type using another stored procedure [flw].[GetFlowType] and assigns the result to the @FlowType variable.

							If the value of @FlowType is ''exp'', it executes two SELECT statements:

							a. The first SELECT statement retrieves the top 5 records for the specified FlowID from the [flw].[SysLogExport] table, sorted by FileName_DW in descending order.

							b. The second SELECT statement retrieves the records from the [flw].[SysLogExport] table, grouped by FlowID, FilePath_DW, and FileName_DW, 
							with the maximum value of ExportDate as IngestionDate. It then filters the result set by the specified FlowID and orders it by FileName_DW in descending order.

							If the value of @FlowType is not ''exp'', it executes two SELECT statements:

							a. The first SELECT statement retrieves the top 5 records for the specified FlowID from the flw.SysLogFile table, sorted by FileRowDate_DW in descending order.

							b. The second SELECT statement retrieves the records from the flw.SysLogFile table, grouped by FlowID, FileDate_DW, FileName_DW, 
							and FileSize_DW, with the maximum value of FileRowDate_DW as IngestionDate. It then filters the result set by the specified 
							FlowID and orders it by FileDate_DW in descending order.

							The stored procedure returns two result sets for each flow type (''exp'' or not ''exp'') based on the specified FlowID.', 'SCHEMA', N'flw', 'PROCEDURE', N'SearchFiles', NULL, NULL
GO
