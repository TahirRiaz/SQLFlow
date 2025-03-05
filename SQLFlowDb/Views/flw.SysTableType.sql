SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysTableType]
AS
SELECT JSON_VALUE(value, '$.ObjectName') AS ObjectName,
       JSON_VALUE(value, '$.Type') AS [Type],
	   CAST(JSON_VALUE(value, '$.IgnoreRelations') AS INT) AS [IgnoreRelations]
	   
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'SysTableType';
GO
