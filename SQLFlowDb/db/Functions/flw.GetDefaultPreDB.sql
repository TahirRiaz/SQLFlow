SET QUOTED_IDENTIFIER ON
GO
SET ANSI_NULLS ON
GO
CREATE FUNCTION [flw].[GetDefaultPreDB] ()
RETURNS nvarchar(255)
AS
BEGIN

    RETURN (SELECT TOP 1 [DefaultPreIngTrgDbName] FROM [flw].[SysDefaultInitPipelineValues])
END;
GO
