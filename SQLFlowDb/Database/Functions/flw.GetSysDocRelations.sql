SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO


CREATE FUNCTION [flw].[GetSysDocRelations]
(
    -- Add the parameters for the function here
    @ObjectName NVARCHAR(255)
)
RETURNS VARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(MAX) = N'',
            @counter INT = 0;
    WITH BASE
    AS (SELECT '| ' + 'LeftObject' + ' | ' + 'LeftObjectCol' + ' | ' + 'RightObject' + ' | ' + 'RightObjectCol' + ' |' AS TableRow
        UNION ALL
        SELECT '| ' + +'--- | ---' + '--- | ---' + '--- | ---' + ' |' AS TableRow
        UNION ALL


        -- Data Rows
        SELECT '| ' + CAST([LeftObject] AS NVARCHAR(255)) + ' | ' + CAST([LeftObjectCol] AS NVARCHAR(255)) + ' | '
               + CAST([RightObject] AS NVARCHAR(255)) + ' | ' + CAST([RightObjectCol] AS NVARCHAR(255)) + ' |'
        FROM [flw].[SysDocRelation]
        WHERE (
                  LeftObject = @ObjectName
                  OR (LeftObject + '.' + QUOTENAME([LeftObjectCol]) = @ObjectName)
              )
              AND RightObject NOT IN
                  (
                      SELECT ObjectName FROM [flw].[SysTableType] WHERE [IgnoreRelations] = 1
                  ))
    SELECT @rValue = @rValue + TableRow + CHAR(13) + CHAR(10),
           @counter = @counter + 1
    FROM BASE;

    IF (@counter < 3)
    BEGIN
        SET @rValue = N'';
    END;

    -- Return the result of the function
    RETURN ISNULL(@rValue, '');
END;
GO
