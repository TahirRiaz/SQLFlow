SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[SysJwtAuthUser]
AS 
SELECT 
    
	JSON_VALUE(value, '$.JwtAuthUserName') AS JwtAuthUserName,
    JSON_VALUE(value, '$.JwtAuthUserPwd') AS [JwtAuthUserPwd],
	[flw].[GetWebApiUrl]() + 'Login' [JwtAuthUrl]
	
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysJwtAuthUser'
GO
