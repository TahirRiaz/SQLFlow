SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO



CREATE FUNCTION [flw].[GetSysDocNote]
(
    -- Add the parameters for the function here
    @ObjectName NVARCHAR(255)
)
RETURNS VARCHAR(MAX)
AS
BEGIN
    -- Declare the return variable here
    DECLARE @rValue NVARCHAR(MAX) = N'', @counter INT = 0

	SELECT @rValue = @rValue + ISNULL([Description],'') +  CHAR(13) + CHAR(10),
			@counter = @counter + 1
	FROM [flw].[SysDocNote]
	WHERE ObjectName = @ObjectName

	IF(@counter = 0)
	BEGIN 
		SET @rValue = ''
	END

    -- Return the result of the function
    RETURN ISNULL(@rValue,'');
END;
GO
