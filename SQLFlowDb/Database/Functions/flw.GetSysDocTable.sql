SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetSysDocTable]
(
    @DataSetName NVARCHAR(255),
    @ObjectName NVARCHAR(255),
    @WithDescription BIT = 0
)
RETURNS TABLE
AS
RETURN
(
    SELECT @DataSetName AS DataSetName,
           s.[ObjectName],
           CASE
               WHEN @WithDescription = 0 THEN
                  '`' + Summary + '`'
               ELSE
                  '`' + REPLACE(REPLACE(SUBSTRING([Description], 1, CHARINDEX('### P', [Description])),'#',''),'`','') +'`'
           END [Documentation],
           '`' + [ObjectDef] +'`' AS [ObjectDef]
    FROM [flw].[SysDoc] s
    WHERE [ObjectName] = @ObjectName
          AND ObjectType = 'Table'
);
GO
