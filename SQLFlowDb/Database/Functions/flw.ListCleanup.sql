SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO

CREATE FUNCTION [flw].[ListCleanup] (@list NVARCHAR(4000))
RETURNS NVARCHAR(4000)
BEGIN
	DECLARE @rValue NVARCHAR(4000)= '';
	--DECLARE @list nvarchar(4000) = ''
	--DECLARE @rValue nvarchar(4000) = ''
	--SET @list = '[Checksum_DW], [CreatedDate_DW], [UpdateDate_DW]'
	SET @list = ','+LTRIM(RTRIM(@list))

	SELECT @rValue = @rValue +','+ RTRIM(LTRIM(item))
	FROM [flw].[StringSplit] ( @list, ',')
	WHERE LEN(RTRIM(LTRIM(item)))>0

	SET @rValue = SUBSTRING(@rValue,2,LEN(@rValue))

    RETURN @rValue;
END;
GO
EXEC sp_addextendedproperty N'MS_Description', '-- Purpose			:   The purpose of the provided user-defined function [flw].[ListCleanup] is to clean up and format a comma-separated list of strings.', 'SCHEMA', N'flw', 'FUNCTION', N'ListCleanup', NULL, NULL
GO
