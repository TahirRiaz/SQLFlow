SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetSysDocObjectType]
(
    -- Add the parameters for the function here
    @ObjectName NVARCHAR(255)
)
RETURNS VARCHAR(255)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(MAX) = N'',
            @counter INT = 0;

    SELECT @rValue = [ObjectType]
	FROM [flw].[SysDoc]
	WHERE ObjectName = @ObjectName 

    -- Return the result of the function
    RETURN @rValue;
END;
GO
