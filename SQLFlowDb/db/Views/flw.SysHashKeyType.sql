SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[SysHashKeyType]
AS 
SELECT 
    
    JSON_VALUE(value, '$.HashKeyType') AS HashKeyType,
	JSON_VALUE(value, '$.DataType') AS DataType,
	JSON_VALUE(value, '$.DataTypeExp') AS DataTypeExp
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysHashKeyType'
GO
