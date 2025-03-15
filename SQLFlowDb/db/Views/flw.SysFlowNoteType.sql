SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE VIEW [flw].[SysFlowNoteType]
AS 
SELECT 
    
    JSON_VALUE(value, '$.FlowNoteType') AS FlowNoteType
    
FROM [flw].[SysCFG]
CROSS APPLY OPENJSON([ParamJsonValue])
WHERE  ParamName = 'SysFlowNoteType'
GO
