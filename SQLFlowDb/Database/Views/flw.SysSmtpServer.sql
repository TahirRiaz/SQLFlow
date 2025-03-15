SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[SysSmtpServer]
AS 
SELECT 
    
    JSON_VALUE(value, '$.Host') AS Host,
	JSON_VALUE(value, '$.Port') AS [Port],
	JSON_VALUE(value, '$.Ssl') AS [Ssl],
	JSON_VALUE(value, '$.User') AS [User],
	JSON_VALUE(value, '$.Password') AS [Password]

FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysSmtpServer'
GO
