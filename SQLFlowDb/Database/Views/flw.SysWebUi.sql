SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[SysWebUi]
AS
SELECT JSON_VALUE(value, '$.WebUiUrl') AS WebUiUrl
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'WebApi';
GO
