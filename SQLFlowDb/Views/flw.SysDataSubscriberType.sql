SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE VIEW [flw].[SysDataSubscriberType]
AS
SELECT JSON_VALUE(value, '$."SubscriberType"') AS SubscriberType
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'SysDataSubscriberType';
GO
