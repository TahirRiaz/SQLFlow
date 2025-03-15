SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE FUNCTION [flw].[GetSysDocDependsOnBy]
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
    ;WITH BASE
    AS (SELECT '| ' + 'DependsOnType' + ' | ' + 'DependsOnName' + ' |'  AS TableRow
        UNION ALL
        SELECT '| ' + '--- | ---' + ' |' AS TableRow
        UNION ALL
        -- Data Rows
        SELECT '| ' + [Type] + ' | ' + QUOTENAME([Schema]) + '.' + QUOTENAME([Name]) + ' |' 
        FROM [flw].[SysDocDependsOnBy]
        WHERE [ObjectName] = @ObjectName)
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
