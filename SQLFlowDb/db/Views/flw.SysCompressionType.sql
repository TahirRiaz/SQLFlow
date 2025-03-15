SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysCompressionType]
AS 
SELECT 
    
    JSON_VALUE(value, '$.CompressionType') AS CompressionType
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysCompressionType'
GO
