SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE VIEW [flw].[SysJwtSettings]
AS
SELECT JSON_VALUE(value, '$.SecretKey') AS SecretKey,
       JSON_VALUE(value, '$.Issuer') AS Issuer,
       JSON_VALUE(value, '$.Audience') AS Audience,
       JSON_VALUE(value, '$.ExpireMinutes') AS ExpireMinutes,
       JSON_VALUE(value, '$.ExpireYears4LongLived') AS ExpireYears4LongLived
FROM [flw].[SysCFG]
    CROSS APPLY OPENJSON([ParamJsonValue])
WHERE ParamName = 'SysJwtSettings';
GO
