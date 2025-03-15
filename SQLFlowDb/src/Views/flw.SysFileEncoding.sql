SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysFileEncoding]
AS 
SELECT 
    
    JSON_VALUE(value, '$.Encoding') AS Encoding,
	JSON_VALUE(value, '$.EncodingName') AS EncodingName
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysFileEncoding'
GO
