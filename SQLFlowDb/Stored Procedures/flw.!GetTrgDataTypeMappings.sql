SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE PROCEDURE [flw].[!GetTrgDataTypeMappings]

AS


BEGIN

DECLARE @FlowID VARCHAR(255) = 45 
DECLARE @MaskDecimal VARCHAR(255)  = 'CAST(REPLACE(REPLACE(@ColName, '','', ''.''), '' '', '''') AS DECIMAL(16,3))'
DECLARE @MaskInt VARCHAR(255)  = 'CAST(REPLACE(@ColName, '' '', '''') AS INT)'
DECLARE @MaskDateTime VARCHAR(255) = 'convert(DATETIME, @ColName, 104)'


;WITH base
AS
(
SELECT '['+ COLUMN_NAME + ']' AS COLUMN_NAME, DATA_TYPE, ORDINAL_POSITION,
CASE WHEN DATA_TYPE = 'int' THEN @MaskInt
	 WHEN DATA_TYPE = 'decimal' THEN @MaskDecimal
	 WHEN DATA_TYPE = 'datetime' THEN @MaskDecimal END AS [SelectExp]
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'Archive'
AND TABLE_NAME = 'Baatbooking_Sess'
AND COLUMN_NAME NOT IN ('Checksum_DW','CreatedDate_DW','UpdateDate_DW')
AND COLUMN_NAME NOT like ('PK_%')
)

SELECT ';WITH BASE AS (' cmd
UNION all
SELECT 'SELECT ' +CHAR(39)+ COLUMN_NAME +CHAR(39)+ ' AS COLUMN_NAME, ' +CHAR(39)+ REPLACE([SelectExp],CHAR(39),CHAR(39)+CHAR(39))+CHAR(39)+ ' AS SelectExp' + ' UNION ALL' AS cmd
FROM base 
WHERE [SelectExp] IS NOT NULL
UNION all
SELECT ') Update trg set trg.[SelectExp] = src.[SelectExp] FROM BASE src INNER JOIN [flw].[PreIngestionTransfrom] trg ON src.COLUMN_NAME = trg.[ColName] AND trg.[FlowID] = ' + CAST(@FlowID AS VARCHAR(255)) cmd
 --PATH('Map') -- ROOT('SQLFlow')






end
GO
