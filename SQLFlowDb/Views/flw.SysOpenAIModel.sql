SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysOpenAIModel]
AS 
SELECT 
    
    JSON_VALUE(value, '$.Model') AS Model,
	CAST(JSON_VALUE(value, '$.MaxTokens') AS INT) AS MaxTokens,
	CAST(JSON_VALUE(value, '$.MinTokens') AS INT) AS MinTokens
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysOpenAIModel'
GO
