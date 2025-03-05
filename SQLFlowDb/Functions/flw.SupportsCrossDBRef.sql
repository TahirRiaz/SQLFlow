SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


/*
  ##################################################################################################################################################
  -- Name				:   [flw].[SupportsCrossDBRef]
  -- Date				:   2022.03.02
  -- Author				:   Tahir Riaz
  -- Company			:   Business IQ
  -- Purpose			:   This function can be used to determine whether cross-database queries are supported by a given data source associated with a FlowID.

  -- Summary			:	

  ##################################################################################################################################################
  -- Ver  User				Date			Change
  -- 1.0  Tahir Riaz		2022.03.02		Initial
  ##################################################################################################################################################
*/
CREATE FUNCTION [flw].[SupportsCrossDBRef]
(
    @FlowID INT
)
RETURNS BIT
BEGIN

    DECLARE @rValue BIT;


    ;WITH base
    AS (SELECT DISTINCT
               ConnectionString AS ConnectionString,
               ISNULL([SupportsCrossDBRef], 0)
AS             [SupportsCrossDBRef]
        FROM [flw].[SysDataSource] ds
            LEFT OUTER JOIN [flw].[Ingestion] ing
                ON ing.[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionCSV] csv
                ON csv.[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionXLS] xls
                ON xls.[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionXLS] [xml]
                ON [xml].[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionPRQ] [prq]
                ON [prq].[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionPRC] [prc]
                ON [prc].[trgServer] = ds.Alias
            LEFT OUTER JOIN [flw].[PreIngestionJSN] [jsn]
                ON [xml].[trgServer] = ds.Alias
        WHERE COALESCE(ing.FlowID, csv.FlowID, xls.FlowID, [xml].FlowID, [jsn].FlowID, [prq].FlowID) = @FlowID)

    --database.windows.net is used for azure db

    SELECT @rValue = CASE
                         WHEN ConnectionString LIKE '%database.windows.net%'
                              AND ISNULL([SupportsCrossDBRef], 0) = 0 THEN
                             0
                         ELSE
                             1
                     END
    FROM base;

    IF (@rValue IS NULL)
        SET @rValue = 0;

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   This function can be used to determine whether cross-database queries are supported by a given data source associated with a FlowID.', 'SCHEMA', N'flw', 'FUNCTION', N'SupportsCrossDBRef', NULL, NULL
GO
