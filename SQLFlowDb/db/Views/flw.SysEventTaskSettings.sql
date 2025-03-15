SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO




CREATE VIEW [flw].[SysEventTaskSettings]
AS
SELECT JSON_VALUE(value, '$.refreshLineageAfterMinutes') AS refreshLineageAfterMinutes,
       JSON_VALUE(value, '$.maxParallelTasks') AS maxParallelTasks,
       JSON_VALUE(value, '$.maxParallelSteps') AS maxParallelSteps
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'SysEventTaskSettings';
GO
