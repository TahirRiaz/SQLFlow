SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE VIEW [flw].[SysWebApi]
AS
SELECT JSON_VALUE(value, '$.WebApiUrl') AS WebApiUrl,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.Login') AS [Login],
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.Logout') AS Logout,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.CheckAuth') AS CheckAuth,
	   JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.ValidateToken') AS ValidateToken,
	   JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.CancelProcess') AS CancelProcess,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.Assertion') AS Assertion,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.HealthCheck') AS HealthCheck,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.SourceControl') AS SourceControl,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.LineageMap') AS LineageMap,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.FlowProcess') AS FlowProcess,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.FlowNode') AS FlowNode,
       JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.FlowBatch') AS FlowBatch,
	   JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.TrgTblSchema') AS TrgTblSchema,
	   JSON_VALUE(value, '$.WebApiUrl') + JSON_VALUE(value, '$.DetectUniqueKey') AS DetectUniqueKey
	   
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'WebApi';
GO
