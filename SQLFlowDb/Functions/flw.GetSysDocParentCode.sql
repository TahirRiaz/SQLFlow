SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetSysDocParentCode]
(
    -- Add the parameters for the function here
    @ObjectName NVARCHAR(255)
)
RETURNS VARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(MAX) = N'', @counter INT = 0

	SELECT 
			@rValue =ObjectDef
		FROM 
			[flw].[SysDoc]
		WHERE Objectname = QUOTENAME(PARSENAME(@ObjectName,3)) +'.'+QUOTENAME(PARSENAME(@ObjectName,2))

    -- Return the result of the function
    RETURN ISNULL(@rValue,'');
END;
GO
