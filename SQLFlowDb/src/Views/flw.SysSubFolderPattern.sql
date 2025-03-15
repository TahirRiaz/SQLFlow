SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysSubFolderPattern]
AS 
SELECT 
    
    JSON_VALUE(value, '$.SubFolderPattern') AS SubFolderPattern,
	JSON_VALUE(value, '$.SubFolderPatternName') AS SubFolderPatternName
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysSubFolderPattern'
GO
