SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysFlowType]
AS
SELECT JSON_VALUE(value, '$.FlowType') AS FlowType,
       JSON_VALUE(value, '$."Description"') AS "Description",
       CAST(JSON_VALUE(value, '$."HasPreIngestionTransform"') AS BIT) AS HasPreIngestionTransform,
       CAST(JSON_VALUE(value, '$."SrcIsFile"') AS BIT) AS SrcIsFile
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'SysFlowType';
GO
