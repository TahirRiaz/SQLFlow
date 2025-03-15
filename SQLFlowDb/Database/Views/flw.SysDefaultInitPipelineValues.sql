SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysDefaultInitPipelineValues]
AS
SELECT JSON_VALUE(value, '$.DefaultBatch') AS DefaultBatch,
       JSON_VALUE(value, '$.DefaultSysAlias') AS DefaultSysAlias,
       JSON_VALUE(value, '$.DefaultPreIngServicePrincipalAlias') AS DefaultPreIngServicePrincipalAlias,
       JSON_VALUE(value, '$.DefaultPreIngSrcPath') AS DefaultPreIngSrcPath,
       JSON_VALUE(value, '$.DefaultPreIngSrcFileName') AS DefaultPreIngSrcFileName,

       JSON_VALUE(value, '$.DefaultPreIngTrgServer') AS DefaultPreIngTrgServer,
       JSON_VALUE(value, '$.DefaultPreIngTrgDbName') AS DefaultPreIngTrgDbName,
       JSON_VALUE(value, '$.DefaultPreIngTrgSchema') AS DefaultPreIngTrgSchema,
	   JSON_VALUE(value, '$.DefaultPreIngTrgTable') AS DefaultPreIngTrgTable,

	   JSON_VALUE(value, '$.DefaultSrcServicePrincipalAlias') AS DefaultSrcServicePrincipalAlias,
	   
       JSON_VALUE(value, '$.DefaultSrcServer') AS DefaultSrcServer,
       JSON_VALUE(value, '$.DefaultSrcDbName') AS DefaultSrcDbName,
       JSON_VALUE(value, '$.DefaultSrcSchema') AS DefaultSrcSchema,
	   JSON_VALUE(value, '$.DefaultSrcObject') AS DefaultSrcObject,
	   

       JSON_VALUE(value, '$.DefaultTrgServer') AS DefaultTrgServer,
       JSON_VALUE(value, '$.DefaultTrgDbName') AS DefaultTrgDbName,
       JSON_VALUE(value, '$.DefaultTrgSchema') AS DefaultTrgSchema,
	   JSON_VALUE(value, '$.DefaultTrgTable') AS DefaultTrgTable,


       JSON_VALUE(value, '$.DefaultTrgServicePrincipalAlias') AS DefaultTrgServicePrincipalAlias,
       JSON_VALUE(value, '$.DefaultTrgPath') AS DefaultTrgPath,
	   JSON_VALUE(value, '$.DefaultTrgFileName') AS DefaultTrgFileName

FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'DefaultInitPipelineValues';
GO
