SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysExportBy]
AS 
SELECT 
    
    JSON_VALUE(value, '$.ExportByName') AS ExportByName,
	JSON_VALUE(value, '$.ExportBy') AS ExportBy
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysExportBy'
GO
